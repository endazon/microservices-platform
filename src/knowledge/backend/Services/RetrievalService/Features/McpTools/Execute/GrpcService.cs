using System.Text.Json;
using Grpc.Core;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Observability;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.McpTools.Declare;
using RetrievalService.Features.Search.Hybrid;
using Pb = Platform.Shared.Contracts.Grpc.Mcp.V1;

namespace RetrievalService.Features.McpTools.Execute;

// FR-16, FR-03, FR-05, UC-08, NFR-09, NFR-16, ADR-0024 §2〜§4, ADR-0034 決定 1・9, 計画 ADR-0086 決定 1・4,
// ADR-0088 決定 1, ADR-0117 決定 1〜3, [[IADR-0292]], [[IADR-0379]] 決定 4, [[IADR-0416]], [[IADR-0426]], [[IADR-0462]], [[IADR-0479]] (#1611):
// **MCP のツールの実行口**（`platform.mcp.v1.McpToolExecution/Execute`）。RetrievalService が申告した
// `retrieval.search_documents` を実行する。
//
// 🔴 **本体は持たない。** 検索は REST `POST /search`・gRPC `DocumentSearch/Search` と**同じ関数**
//   （`SearchEndpoint.ExecuteAsync`）を通る（検索を 2 つにしない）。ここに在るのは、利用者文脈の検証・
//   ツールの引数の解釈・応答の共通エンベロープへの写像だけである。
//
// 🔴 **判定の順番**（どれも前段で落ちたら後段を 1 度も走らせない）:
//   1. `ServiceCaller`（面の門。利用者のトークンは管理者でも通らない）
//   2. **呼び出し元が MCP サーバーか**（許可集合 `McpToolExecutionRelayOptions`。既定 `mcp-server` だけ）。
//      他の `platform-service` の主体が本文の利用者文脈を運んでも信じない（PERMISSION_DENIED）。
//   3. 利用者文脈の有無（無い・`user_id` が空は INVALID_ARGUMENT。機械の主体へ読み替えない）
//   4. **自分の申告に在るツールか**（無ければ NOT_FOUND。宛先は申告したサービス＋ツール名。ADR-0117 決定 1）
//   5. **操作の突合**: 操作は受け口が自分のツールから決める（検索は `read`）。本文の `action` が違えば INVALID_ARGUMENT ——
//      本文の値で判定を緩めさせない
//   6. 引数の検証（丸めない。INVALID_ARGUMENT）
//   7. 🔴 **自分で認可する**: 本文の `user_id` で認可サービスへ判定を問う（`ISearchAccessResolver.ResolveForUserAsync`）。
//      **属性は送らない**（空）—— 認可サービスが利用者名から引き直す（ADR-0088）。
//      **解決済みの scope を受け取る項目は proto に無い**（番号 3 は予約済み。旧い呼び出し元が載せても読み飛ばされる）。
//   8. 検索（同じ関数）→ サービスアカウント実行なら個人資料を落とす → 文書単位へ畳む
//
// 🔴 **ADR-0034 決定 9（要求側の 1 層目）**: `user_id` が `service-account-` で始まるならサービスアカウント実行であり、
//   個人資料（`doc_scope=private-note`）を 1 件も返さない。MCP サーバーの応答側のフィルタ（2 層目）とは別に持つ。
// 🔴 ［2026-09-28 追記 / #1671］エンベロープの属性は共有の許可リスト（`McpEnvelopeAttributes`）のキーだけを写す。
// 🔴 **「該当が無い」と「権限が無い」を区別させない**（存在秘匿）—— どちらも空の文書の並びで返る。
//   件数（`total_count`）は判定と除外を通したあとの件数である（ADR-0034 決定 4）。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class McpToolExecutionGrpcService(
    IHybridSearchService search,
    ISearchAccessResolver access,
    IOptions<McpToolExecutionRelayOptions> relay,
    ILogger<McpToolExecutionGrpcService> logger)
    : Pb.McpToolExecution.McpToolExecutionBase
{
    /// <summary>`retrieval.search_documents` の申告名。申告（`McpToolDeclarationSource`）と同じ綴り（試験で一致を固定する）。</summary>
    public const string SearchDocumentsTool = "retrieval.search_documents";

    /// <summary>検索が要する操作（検索は閲覧経路しか持たない。`SearchUserContext.ReadAction` と同じ値）。</summary>
    public const string ReadAction = SearchUserContext.ReadAction;

    // 引数の上限・既定は申告の `input_schema`（minimum 1・maximum 50・default 10）と同じ値である（試験で一致を固定する）。
    public const int MinLimit = 1;
    public const int MaxLimit = 50;
    public const int DefaultLimit = 10;

    private static readonly IReadOnlyDictionary<string, string> NoAttributes = new Dictionary<string, string>();

    public override async Task<Pb.McpToolResult> Execute(Pb.ExecuteMcpToolRequest request, ServerCallContext context)
    {
        // 2. 🔴 呼び出し元が MCP サーバーか。利用者文脈の検証・ツールの突合より前に置く
        //    （信頼しない呼び出し元に、どの形なら通るかを 1 つも見せない）。
        EnsureTrustedRelay(context);

        // 3. 利用者文脈。🔴 空を deny へ畳まない —— 呼び出し元の配線誤り（文脈の積み忘れ）が「該当なし」に化ける。
        if (request.User is null || string.IsNullOrWhiteSpace(request.User.UserId))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "user.user_id は必須である（利用者文脈が無い実行は受けない）。"));

        // 4. 自分の申告に在るツールか（申告から落とした候補・他のサービスのツールは実行しない）。
        if (!IsDeclared(request.Tool) || !string.Equals(request.Tool, SearchDocumentsTool, StringComparison.Ordinal))
            throw new RpcException(new Status(StatusCode.NotFound, "このサービスが申告したツールではありません。"));

        // 5. 🔴 操作は受け口が決める。本文の `action` は突き合わせるだけで、判定には使わない。
        if (!string.Equals(request.User.Action, ReadAction, StringComparison.Ordinal))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"user.action がツールの要する操作（{ReadAction}）と一致しない。"));

        // 6. 引数（丸めない）。
        var (query, limit) = ParseSearchArguments(request.ArgumentsJson);

        var userId = request.User.UserId;

        // 7. 🔴 自分で認可する。属性は送らない（認可サービスが引き直す。ADR-0088）。
        var authoritative = await access.ResolveForUserAsync(userId, NoAttributes, context.CancellationToken);
        var effective = ScopeNarrowing.Resolve(authoritative, rangeFilters: null);

        // 8. 検索は REST / gRPC の検索と同じ関数を通る。利用者の資格情報は転送できない（`FromBody`）。
        // ［2026-10-11 / #1871］[[IADR-0534]]: ツールの結果へ縮退の印は写さない（ツールの契約外。計器とログには残る）。
        var found = await SearchEndpoint.ExecuteAsync(
            search, new SearchRequest(query, limit), effective,
            SearchUserContext.FromBody(userId, NoAttributes), context.CancellationToken);

        return ToResult(found.Results, excludePrivateNote: IsServiceAccount(userId));
    }

    // 🔴 本文の利用者文脈を信じてよいのは、それを運ぶのが**利用者の権限で動く中継者として許可集合に載った
    //   機械クライアント**（MCP サーバー）だからである（ADR-0086 §結果が受け入れた依存の範囲。ADR-0117 決定 3）。
    private void EnsureTrustedRelay(ServerCallContext context)
    {
        var caller = context.GetHttpContext().User;
        if (relay.Value.TrustsUserContextFrom(caller))
            return;

        // 基数は realm の機密クライアント数で閉じる（利用者識別子・引数・ツール名は載せない）。
        logger.LogWarning(
            "McpToolExecution rejected a user context from a caller that is not a trusted relay (client={ClientId}). "
            + "Trusted relays are configured under {Section}:{Key}.",
            MachinePrincipal.ClientIdOf(caller) ?? "(unknown)", McpToolExecutionRelayOptions.SectionName,
            TrustedUserContextRelay.ClientsKey);
        throw new RpcException(new Status(StatusCode.PermissionDenied,
            "この呼び出し元はツールの実行を利用者文脈で依頼できません。"));
    }

    // 申告（個人資料の除外を通したあとの公開し得るツール）に在るか。申告の 1 経路（`Declare`）を通す。
    private static bool IsDeclared(string tool) =>
        McpToolDeclarationSource.Declare().Tools.Any(t => string.Equals(t.Name, tool, StringComparison.Ordinal));

    // ADR-0034 決定 9: Keycloak が client credentials の主体へ付ける利用者名の形。
    internal static bool IsServiceAccount(string userId) =>
        userId.StartsWith(MachinePrincipal.ServiceAccountUsernamePrefix, StringComparison.OrdinalIgnoreCase);

    // 引数の解釈。申告の `input_schema` に従い、`query`（必須・空でない文字列）と `limit`（整数 1〜50。既定 10）だけを読む。
    // 🔴 それ以外の鍵は読まない —— `scope` / `filters` 等を書いても判定にも絞り込みにも効かない（権限は自分で引いたものだけ）。
    // 🔴 範囲外は丸めずに拒否する —— 黙って丸めると、呼び出し側の LLM は指定どおりの件数で探した結果だと信じる。
    internal static (string Query, int Limit) ParseSearchArguments(string argumentsJson)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Invalid("arguments_json が JSON として読めない。");
        }

        if (root.ValueKind != JsonValueKind.Object)
            throw Invalid("arguments_json は JSON オブジェクトである必要がある。");

        if (!root.TryGetProperty("query", out var q) || q.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(q.GetString()))
            throw Invalid("query（空でない文字列）は必須である。");

        var limit = DefaultLimit;
        if (root.TryGetProperty("limit", out var l))
        {
            if (l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out limit))
                throw Invalid($"limit は {MinLimit}〜{MaxLimit} の整数である必要がある。");
            if (limit < MinLimit || limit > MaxLimit)
                throw Invalid($"limit は {MinLimit}〜{MaxLimit} の整数である必要がある（丸めない）。");
        }

        return (q.GetString()!, limit);
    }

    private static RpcException Invalid(string message) => new(new Status(StatusCode.InvalidArgument, message));

    // 検索結果（チャンク単位・スコア順）→ 共通エンベロープ（文書単位）。同じ文書のチャンクは最上位の 1 つだけを残す。
    // 本文（`body`）はチャンクの抜粋であり、原本が本文を持たない文書（`HasBody=false`）は null。越境の判定は MCP サーバーが行う。
    // 参照リンク（`reference_url`）は持たない（索引が持つのは内部の格納先であり、利用者へ見せるリンクではない）。
    internal static Pb.McpToolResult ToResult(IReadOnlyList<SearchResultDto> results, bool excludePrivateNote)
    {
        var result = new Pb.McpToolResult();
        var seen = new HashSet<Guid>();
        foreach (var hit in results)
        {
            // 🔴 ADR-0034 決定 9（要求側の 1 層目）: サービスアカウント実行は個人資料を返さない。件数にも含めない。
            if (excludePrivateNote && DocumentScopes.IsPrivateNote(hit.Attributes))
                continue;
            if (!seen.Add(hit.DocumentId))
                continue;

            var document = new Pb.McpToolDocument
            {
                DocumentId = hit.DocumentId.ToString(),
                Title = hit.DocumentTitle,
            };
            // 🔴 ［2026-09-28 追記 / #1671］許可リストのキーだけ（`McpEnvelopeAttributes`。MCP サーバーが応答の統制で読む
            //   `confidentiality`・`doc_scope`・`project`）。`owner`・`dept` 等は MCP サーバーが読まず、外部 LLM へ渡す理由が無い
            //   （越境する個人識別子の最小化。REST / gRPC の検索応答は変えない）。
            foreach (var (key, value) in hit.Attributes)
            {
                if (McpEnvelopeAttributes.IsCarried(key))
                    document.Attributes[key] = value;
            }
            if (hit.HasBody)
                document.Body = hit.Text;
            result.Documents.Add(document);
        }

        result.TotalCount = result.Documents.Count;
        result.Truncated = false;
        return result;
    }
}
