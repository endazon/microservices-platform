using System.Text.Json;
using DocumentService.Features.Documents;
using DocumentService.Features.McpTools.Declare;
using Grpc.Core;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Observability;
using Pb = Platform.Shared.Contracts.Grpc.Mcp.V1;

namespace DocumentService.Features.McpTools.Execute;

// FR-16, FR-06, FR-19, UC-08, UC-03, NFR-09, NFR-16, ADR-0024 §2〜§4, ADR-0034 決定 2・4・9, 計画 ADR-0086 決定 1・4,
// ADR-0088 決定 1, ADR-0117 決定 1〜3, ADR-0119 決定 3, ADR-0121 決定 2・4・5, [[IADR-0292]], [[IADR-0379]] 決定 4,
// [[IADR-0476]], [[IADR-0483]], [[IADR-0479]]（2026-09-28 追記 / #1611 段 2）:
// **MCP のツールの実行口**（`platform.mcp.v1.McpToolExecution/Execute`）。DocumentService が申告した
// `document.get_document` / `document.list_documents` を実行する。RetrievalService（段 1）・GraphService（段 3）の実行口と同じ形である。
//
// 🔴 **本体と判定器は持たない。** 読み取りは REST `GET /documents/{id}`・`GET /documents`・gRPC `DocumentRead` と**同じ関数**
//   （`DocumentReadUseCase` → 判定点 `DocumentReadAccess`）を通る。内容の ABAC の門（`IContentAbacGate`）で枝を選ぶのも判定点の中であり、
//   **受け口は門を読まない**（読んで分岐すると判定点が 2 つになる）。門が閉じている間（既定 Off）は #1615 の閉じた枝（従前の判定）、
//   開いた後は認可サービスの `read` の分岐で判定される —— どちらでも REST の同じ利用者の結果を超えない。
//   ここに在るのは、利用者文脈の検証・ツールの引数の解釈・応答の共通エンベロープへの写像だけである。
//
// 🔴 **判定の順番**（どれも前段で落ちたら後段を 1 度も走らせない）:
//   1. `ServiceCaller`（面の門。利用者のトークンは管理者でも通らない）
//   2. **呼び出し元が MCP サーバーか**（許可集合 `McpToolExecutionRelayOptions`。既定 `mcp-server` だけ）。PERMISSION_DENIED
//   3. 利用者文脈の有無（無い・`user_id` が空は INVALID_ARGUMENT。機械の主体へ読み替えない）
//   4. **自分の申告に在るツールか**（無ければ NOT_FOUND。宛先は申告したサービス＋ツール名。ADR-0117 決定 1）
//   5. **操作の突合**: 操作は受け口が決める（2 ツールとも閲覧 = `read`）。本文の `action` が違えば INVALID_ARGUMENT
//   6. 引数の検証（丸めない。INVALID_ARGUMENT）
//   7. 🔴 **自分で認可する**: 本文の `user_id` を主体（`DocumentReadPrincipal.RelayedUser`）にして判定点を通す。認可サービスへは
//      利用者名で問う（`IDocumentReadScopeSource`。**属性は送らない**＝空。認可サービスが引き直す。ADR-0088）。
//   8. 読み取り（同じ関数）→ サービスアカウント実行なら個人資料を落とす → 共通エンベロープ（件数は判定と除外の後）
//
// 🔴 **ADR-0034 決定 9（要求側の 1 層目）**: `user_id` が `service-account-` で始まるならサービスアカウント実行であり、
//   個人資料（`doc_scope=private-note`）を返さない。判定点（`RelayedUser` が機械として扱い、機械は個人資料を読まない）と
//   写像の 2 か所で落とす（多層防御）。MCP サーバーの応答側のフィルタ（2 層目）とは別に持つ。
// 🔴 **エンベロープの属性は許可リスト（`McpEnvelopeAttributes`）のキーだけ**（#1671）。台帳の属性は所有者・部署等を持つ。
// 🔴 **「無い」と「見えない」を区別させない**（ADR-0034 決定 2 / ADR-0056）—— どちらも空の文書の並びで返る。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class McpToolExecutionGrpcService(
    DocumentReadUseCase reads,
    IOptions<McpToolExecutionRelayOptions> relay,
    ILogger<McpToolExecutionGrpcService> logger)
    : Pb.McpToolExecution.McpToolExecutionBase
{
    /// <summary>申告名。申告（`McpToolDeclarationSource`）と同じ綴り（試験で一致を固定する）。</summary>
    public const string GetDocumentTool = "document.get_document";
    public const string ListDocumentsTool = "document.list_documents";

    /// <summary>2 ツールが要する操作（文書取得系は閲覧だけ。判定点が認可サービスへ問う操作と同じ値）。</summary>
    public const string ReadAction = "read";

    // `list_documents` の件数の上限・既定は申告の `input_schema`（minimum 1・maximum 100・default 20）と同じ値である（試験で一致を固定する）。
    public const int MinLimit = 1;
    public const int MaxLimit = 100;
    public const int DefaultLimit = 20;

    private static readonly string[] Tools = [GetDocumentTool, ListDocumentsTool];

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
        var tool = request.Tool;
        if (!IsDeclared(tool) || !Tools.Contains(tool, StringComparer.Ordinal))
            throw new RpcException(new Status(StatusCode.NotFound, "このサービスが申告したツールではありません。"));

        // 5. 🔴 操作は受け口が決める。本文の `action` は突き合わせるだけで、判定には使わない。
        if (!string.Equals(request.User.Action, ReadAction, StringComparison.Ordinal))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"user.action がツールの要する操作（{ReadAction}）と一致しない。"));

        // 6. 引数（丸めない）。
        var args = ParseArguments(tool, request.ArgumentsJson);

        var userId = request.User.UserId;
        var excludePrivateNote = IsServiceAccount(userId);

        // 7・8. 🔴 自分で認可する。主体は本文の利用者（`service-account-` は機械として扱う）。判定は既存の判定点のまま
        //   （門の枝の選択・認可サービスへの問い合わせ・存在秘匿はすべてそちら）。
        //   身元は本文で主張され、s2s の門と許可集合を通っている（ADR-0086 決定 4 が受け入れた依存の範囲）。
        var principal = DocumentReadPrincipal.RelayedUser(userId);
        IReadOnlyList<DocumentDto> visible = tool == GetDocumentTool
            ? await reads.GetAsync(principal, args.DocumentId!.Value, context.CancellationToken) is { } doc ? [doc] : []
            : await reads.ListAsync(principal, context.CancellationToken);

        return ToResult(visible, tool == GetDocumentTool ? 1 : args.Limit, excludePrivateNote);
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

    // ADR-0034 決定 9: Keycloak が client credentials の主体へ付ける利用者名の形（`DocumentReadPrincipal.RelayedUser` と同じ判定）。
    internal static bool IsServiceAccount(string userId) =>
        userId.StartsWith(MachinePrincipal.ServiceAccountUsernamePrefix, StringComparison.OrdinalIgnoreCase);

    internal sealed record ToolArguments(Guid? DocumentId, int Limit);

    // 引数の解釈。申告の `input_schema` に従い、`get_document` は `document_id`（必須・GUID の文字列）、
    // `list_documents` は `limit`（整数 1〜100。省略は 20）だけを読む。
    // 🔴 それ以外の鍵は読まない —— `scope` / `filters` 等を書いても判定にも絞り込みにも効かない（権限は判定点が引いたものだけ）。
    // 🔴 範囲外は丸めずに拒否する —— 黙って丸めると、呼び出し側の LLM は指定した件数で引いた結果だと信じる（11_mcp-server-integration §6）。
    internal static ToolArguments ParseArguments(string tool, string argumentsJson)
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

        if (tool == GetDocumentTool)
        {
            if (!root.TryGetProperty("document_id", out var d) || d.ValueKind != JsonValueKind.String
                || !Guid.TryParse(d.GetString(), out var documentId))
                throw Invalid("document_id（GUID の文字列）は必須である。");
            return new ToolArguments(documentId, 1);
        }

        var limit = DefaultLimit;
        if (root.TryGetProperty("limit", out var l))
        {
            if (l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out limit) || limit < MinLimit || limit > MaxLimit)
                throw Invalid($"limit は {MinLimit}〜{MaxLimit} の整数である必要がある（丸めない）。");
        }
        return new ToolArguments(null, limit);
    }

    private static RpcException Invalid(string message) => new(new Status(StatusCode.InvalidArgument, message));

    // 判定点を通った文書（更新の新しい順）→ 共通エンベロープ。
    //   - サービスアカウント実行なら個人資料を落とす（多層防御。件数にも含めない）
    //   - 件数（`total_count`）は判定と除外の後の全体件数。`limit` を超えたら先頭だけを返し `truncated` を立てる
    //   - 題名と属性（許可リストのキーだけ）を返す。台帳は本文を持たず、`MarkdownUri` は内部の格納先であって利用者へ見せる
    //     リンクではないので、`body` / `reference_url` は無い
    internal static Pb.McpToolResult ToResult(IReadOnlyList<DocumentDto> visible, int limit, bool excludePrivateNote)
    {
        var carried = excludePrivateNote
            ? visible.Where(d => !DocumentScopes.IsPrivateNote(d.Attributes)).ToList()
            : visible;

        var result = new Pb.McpToolResult();
        foreach (var dto in carried.Take(limit))
        {
            var document = new Pb.McpToolDocument { DocumentId = dto.Id.ToString(), Title = dto.Title };
            foreach (var (key, value) in dto.Attributes)
            {
                // 🔴 許可リストのキーだけ（`McpEnvelopeAttributes`）。所有者・部署・共有先は運ばない。
                if (McpEnvelopeAttributes.IsCarried(key))
                    document.Attributes[key] = value;
            }
            result.Documents.Add(document);
        }

        result.TotalCount = carried.Count;
        result.Truncated = carried.Count > limit;
        return result;
    }
}
