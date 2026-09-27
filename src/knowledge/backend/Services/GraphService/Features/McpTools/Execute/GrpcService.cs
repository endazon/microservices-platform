using System.Text.Json;
using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Features.Graph.Neighbors;
using GraphService.Features.McpTools.Declare;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Observability;
using Platform.Shared.Kernel;
using Pb = Platform.Shared.Contracts.Grpc.Mcp.V1;

namespace GraphService.Features.McpTools.Execute;

// FR-16, FR-17, UC-08, UC-10, NFR-09, NFR-16, ADR-0024 §2〜§4, ADR-0034 決定 1・2・3・4・9, 計画 ADR-0086 決定 1・4,
// ADR-0088 決定 1, ADR-0117 決定 1〜3, [[IADR-0242]], [[IADR-0292]], [[IADR-0379]] 決定 4, [[IADR-0410]],
// [[IADR-0479]]（2026-09-27 追記 / #1611 段 3）:
// **MCP のツールの実行口**（`platform.mcp.v1.McpToolExecution/Execute`）。GraphService が申告した
// `graph.get_backlinks` / `graph.get_links` / `graph.traverse` を実行する。RetrievalService の実行口（段 1）と同じ形である。
//
// 🔴 **本体と判定器は持たない。** 探索は REST `GET /graph/{id}/neighbors`・gRPC `GraphNeighbors/ExpandNeighbors` と**同じ関数**
//   （`ExpandNeighborsUseCase`）を通る。ホップごとの ABAC（`AuthorizedNode` の型ゲート）・出力ゲート（`GraphViewResponse.Seal`）・
//   存在秘匿・hops の上限はすべてそちらに在り、**第二の判定点をここに作らない**。ここに在るのは、利用者文脈の検証・
//   ツールの引数の解釈・応答の共通エンベロープへの写像だけである。
//
// 🔴 **判定の順番**（どれも前段で落ちたら後段を 1 度も走らせない）:
//   1. `ServiceCaller`（面の門。利用者のトークンは管理者でも通らない）
//   2. **呼び出し元が MCP サーバーか**（許可集合 `McpToolExecutionRelayOptions`。既定 `mcp-server` だけ）。PERMISSION_DENIED
//   3. 利用者文脈の有無（無い・`user_id` が空は INVALID_ARGUMENT。機械の主体へ読み替えない）
//   4. **自分の申告に在るツールか**（無ければ NOT_FOUND。宛先は申告したサービス＋ツール名。ADR-0117 決定 1）
//   5. **操作の突合**: 操作は受け口が決める（3 ツールとも閲覧 = `read`）。本文の `action` が違えば INVALID_ARGUMENT
//   6. 引数の検証（丸めない。INVALID_ARGUMENT）
//   7. 🔴 **自分で認可する**: 本文の `user_id` で認可サービスへ判定を問う（`ExpandNeighborsUseCase` の中の
//      `IGraphAccessResolver.ResolveForUserAsync`。**属性は送らない**＝空。認可サービスが利用者名から引き直す。ADR-0088）。
//      認可サービス不達は既存の縮退で `Granted=false` になり、1 件も返らない。
//   8. 探索（同じ関数）→ サービスアカウント実行なら個人資料を起点・中継・結果に使わない → 共通エンベロープ
//
// 🔴 **ADR-0034 決定 9（要求側の 1 層目）**: `user_id` が `service-account-` で始まるならサービスアカウント実行であり、
//   個人資料（`doc_scope=private-note`）を**探索の中で刈る**（`excludePrivateNote`。橋にもしない）。応答の写像でも落とす（多層防御）。
//   MCP サーバーの応答側のフィルタ（2 層目）とは別に持つ。
// 🔴 **「無い」と「見えない」を区別させない**（ADR-0034 決定 2）—— 起点が無い・見えない・権限が無いはどれも空の文書の並びで返る。
//   件数（`total_count`）は判定と除外を通したあとの件数である（ADR-0034 決定 4）。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
internal sealed class McpToolExecutionGrpcService(
    ExpandNeighborsUseCase neighbors,
    IOptions<McpToolExecutionRelayOptions> relay,
    ILogger<McpToolExecutionGrpcService> logger)
    : Pb.McpToolExecution.McpToolExecutionBase
{
    /// <summary>申告名。申告（`McpToolDeclarationSource`）と同じ綴り（試験で一致を固定する）。</summary>
    public const string GetBacklinksTool = "graph.get_backlinks";
    public const string GetLinksTool = "graph.get_links";
    public const string TraverseTool = "graph.traverse";

    /// <summary>3 ツールが要する操作（探索系は閲覧だけ。`GraphAccessAction.Read` と同じ値）。</summary>
    public const string ReadAction = GraphAccessAction.Read;

    // 被参照・参照先は起点から 1 ホップの辺だけを見る。
    private const int DirectLinkHops = 1;

    private static readonly IReadOnlyDictionary<string, string> NoAttributes = new Dictionary<string, string>();

    // 🔴 ［2026-09-27 追記 / #1611 段 3 監査 B-1］**エンベロープへ載せる属性は許可リストのキーだけ**（MCP サーバーが応答の統制で読む
    // `confidentiality`・`doc_scope`・`project`）。`shared_with`・`owner`・部署等の ABAC 判定用の属性は運ばない —— MCP サーバーは attributes を
    // そのまま外部クライアントへ返し、共有先は所有者にだけ返す規則（ADR-0098 / IADR-0450）を迂回する。
    // ［2026-09-28 追記 / #1671］許可リストは受け口ごとに持たず、共有の定数 `McpEnvelopeAttributes`（Platform.Shared.Contracts）を参照する
    // （MCP サーバーの読み手と同じ定数。キーを足すとき片側だけ変わる割れ方を防ぐ）。

    private static readonly string[] Tools = [GetBacklinksTool, GetLinksTool, TraverseTool];

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

        // 7・8. 🔴 自分で認可する（属性は空。認可サービスが引き直す）。判定・探索・出力ゲートは既存の本体のまま。
        //   `IsAuthenticated=true`: 身元は本文で主張され、s2s の門と許可集合を通っている（ADR-0086 決定 4 が受け入れた依存の範囲）。
        var outcome = await neighbors.ExecuteAsync(
            args.DocumentId,
            tool == TraverseTool ? args.Hops : DirectLinkHops,
            by: null,
            types: args.EdgeTypes,
            new GraphUserContext(userId, NoAttributes, IsAuthenticated: true),
            context.CancellationToken,
            excludePrivateNote);

        if (outcome.IsFailure)
        {
            // 本体の検証（hops・types）は上の引数の検証と同じ値域なので、ここへ来るのは食い違いだけ。丸めずに返す。
            // INTERNAL は固定文言（内部の失敗の中身を外へ出さない。監査 N-4）。
            if (outcome.Error.Kind == ErrorKind.Validation)
                throw new RpcException(new Status(StatusCode.InvalidArgument, outcome.Error.Message));
            throw new RpcException(new Status(StatusCode.Internal, "ツールの実行に失敗しました。"));
        }

        return outcome.Value.View is { } view
            ? ToResult(tool, args.DocumentId, view, excludePrivateNote)
            : Empty();
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

    internal sealed record ToolArguments(Guid DocumentId, int? Hops, string? EdgeTypes);

    // 引数の解釈。申告の `input_schema` に従い、`document_id`（必須・GUID の文字列）と、`graph.traverse` だけ
    // `hops`（整数 1〜上限。省略は本体の既定）・`edge_types`（GUID の文字列の配列）を読む。
    // 🔴 それ以外の鍵は読まない —— `scope` / `filters` 等を書いても判定にも絞り込みにも効かない（権限は自分で引いたものだけ）。
    // 🔴 範囲外は丸めずに拒否する —— 黙って丸めると、呼び出し側の LLM は指定したホップ数まで探した結果だと信じる
    //   （11_mcp-server-integration §6。値域は `GraphTraversal` の定数＝申告と同じ）。
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

        if (!root.TryGetProperty("document_id", out var d) || d.ValueKind != JsonValueKind.String
            || !Guid.TryParse(d.GetString(), out var documentId))
            throw Invalid("document_id（GUID の文字列）は必須である。");

        if (tool != TraverseTool)
            return new ToolArguments(documentId, null, null);

        int? hops = null;
        if (root.TryGetProperty("hops", out var h))
        {
            if (h.ValueKind != JsonValueKind.Number || !h.TryGetInt32(out var value)
                || value < 1 || value > GraphTraversal.MaxHops)
                throw Invalid($"hops は 1〜{GraphTraversal.MaxHops} の整数である必要がある（丸めない）。");
            hops = value;
        }

        string? edgeTypes = null;
        if (root.TryGetProperty("edge_types", out var t))
        {
            if (t.ValueKind != JsonValueKind.Array)
                throw Invalid("edge_types は辺の型 ID（GUID の文字列）の配列である必要がある。");
            var ids = new List<string>();
            foreach (var item in t.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var id))
                    throw Invalid("edge_types の要素は辺の型 ID（GUID の文字列）である必要がある。");
                ids.Add(id.ToString("D"));
            }
            // 空の配列は「絞らない」（REST の `types` の空と同じ）。
            if (ids.Count > 0)
                edgeTypes = string.Join(NeighborsQueryValidator.TypesSeparator, ids);
        }

        return new ToolArguments(documentId, hops, edgeTypes);
    }

    private static RpcException Invalid(string message) => new(new Status(StatusCode.InvalidArgument, message));

    private static Pb.McpToolResult Empty() => new() { TotalCount = 0, Truncated = false };

    // `Seal` 済みの応答（出力ゲートを通ったノードと、両端が見える辺だけ）→ 共通エンベロープ。
    //   - `graph.traverse`: 起点を除く到達文書（到達順）
    //   - `graph.get_links`: 起点が参照する文書（起点 → 相手の辺。`Edge` の Source → Target が意味方向）
    //   - `graph.get_backlinks`: 起点を参照する文書（相手 → 起点の辺。Target の逆引き）
    // 題名と属性（`Seal` が通したノードの複製）を返す。グラフは本文を持たないので `body` / `reference_url` は無い。
    internal static Pb.McpToolResult ToResult(string tool, Guid origin, GraphViewResponse view, bool excludePrivateNote)
    {
        IReadOnlySet<Guid> wanted = tool switch
        {
            GetLinksTool => view.Edges.Where(e => e.SourceDocumentId == origin).Select(e => e.TargetDocumentId).ToHashSet(),
            GetBacklinksTool => view.Edges.Where(e => e.TargetDocumentId == origin).Select(e => e.SourceDocumentId).ToHashSet(),
            _ => view.Nodes.Select(n => n.DocumentId).ToHashSet(),
        };

        var result = new Pb.McpToolResult();
        foreach (var node in view.Nodes)
        {
            if (node.DocumentId == origin || !wanted.Contains(node.DocumentId))
                continue;
            var attributes = view.NodeAttributes.GetValueOrDefault(node.DocumentId) ?? NoAttributes;
            // 🔴 ADR-0034 決定 9（多層防御）: 探索で刈っているが、写像でも個人資料を落とす。件数にも含めない。
            if (excludePrivateNote && (node.IsPrivateNote || GraphDocumentScope.IsPrivateNote(attributes)))
                continue;

            var document = new Pb.McpToolDocument { DocumentId = node.DocumentId.ToString(), Title = node.Title };
            foreach (var (key, value) in attributes)
            {
                // 🔴 許可リストのキーだけ（`McpEnvelopeAttributes`）。共有先・所有者は運ばない。
                if (McpEnvelopeAttributes.IsCarried(key))
                    document.Attributes[key] = value;
            }
            result.Documents.Add(document);
        }

        // 件数は判定後の件数（ADR-0034 決定 4）。［監査 N-1］近傍探索で表示上限に打ち切ったら、許可済みの全体件数（`TotalNodes`。
        // 起点を含むので 1 を引く）を返す（11_mcp-server-integration §6）。被参照・参照先は辺の向きごとの全体件数を本体が数えないので、
        // 打ち切り時も返した件数のまま（`truncated` で打ち切りを示す）。
        result.TotalCount = tool == TraverseTool && view.Truncated
            ? Math.Max(result.Documents.Count, view.TotalNodes - 1)
            : result.Documents.Count;
        result.Truncated = view.Truncated;
        return result;
    }
}
