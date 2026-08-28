using System.Globalization;
using System.Text.Json.Serialization;
using GraphService.Domain;
using Knowledge.Contracts.Dtos;

namespace GraphService.Features.McpTools;

// FR-16, ADR-0024 §2, [[IADR-0292]]: `GET /internal/mcp-tools` が返す自己申告の形。
//
// 🔴 **契約は新設していない。** McpServer の `Domain/McpToolContracts.cs` の**ワイヤ形式に
// そのまま合わせた写し**である。共有化しない理由は `GraphDocumentScope` と同じ
// （可変ユニットから platform の McpServer は参照できない。IADR-0274 §検討した選択肢）。
// `Platform.Shared.Contracts` への昇格（IADR-0269 決定 6）は [[IADR-0292]] 決定 3 で先送りした。
public sealed record McpToolDeclaration(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("input_schema")] string InputSchema,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("required_scope")] string RequiredScope,
    [property: JsonPropertyName("egress_class")] string EgressClass);

// FR-16, ADR-0024 §2: 1 サービスぶんの自己申告。
public sealed record ServiceToolDeclarations(
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("tools")] IReadOnlyList<McpToolDeclaration> Tools);

// FR-16, FR-19, SC-12, ADR-0024（2026-08-02 注記）, ADR-0034 決定 9:
// 申告し**得る**ツールと、そのツールが対象とする文書スコープの対。
public sealed record McpToolCandidate(
    IReadOnlyDictionary<string, string> Coverage,
    McpToolDeclaration Declaration);

// FR-16, FR-17, ADR-0024 2026-08-01 注記, 11_mcp-server-integration §6: GraphService の自己申告。
//
// 🔴 **公開するのは探索系（読み取り）の 3 つだけである** —— `get_backlinks` / `get_links` /
// `traverse`。**要約系（`get_cluster_summary`。LLM 呼び出しを伴う）は公開しない**
// （計画の表がそう定める。AI 分析系を初期公開に含めない方針の適用結果）。本サービスに実体も無い。
// 万一 `ai.*` や `*get_cluster_summary` が公開構成へ紛れても
// `ToolPublicationConfigValidator` が起動時に弾く（二重の防壁）。
public static class McpToolDeclarationSource
{
    // FR-15 の `/internal/introspection` と同じサービス名を使う（同じ規約系に置くため）。
    public const string ServiceName = "graph-service";

    public const string SelfBaseUrlKey = "Mcp:SelfBaseUrl";
    public const string DefaultSelfBaseUrl = "http://graph-service:8080";

    // ADR-0024 §5「egress_class 必須」。欠けた申告は McpServer が公開しない（安全側）。
    private const string EgressClass = "internal";

    // 被覆の綴りはユニット共通の語彙（`DocumentScopes`）から採る。**判定側は
    // `GraphDocumentScope.IsPrivateNote`（本サービス既存の集合帰属判定）を使う** ——
    // `GraphDocumentScope` は `organization` の定数を持たない（否定で判定しないため必要が無い）。
    private static IReadOnlyDictionary<string, string> Organization { get; } =
        new Dictionary<string, string> { [DocumentScopes.Key] = DocumentScopes.Organization };

    // 申告し得る候補の全体。**除外は下の Publishable が 1 箇所で行う。**
    public static IReadOnlyList<McpToolCandidate> Candidates(string selfBaseUrl)
    {
        var basePath = selfBaseUrl.TrimEnd('/') + "/internal/mcp";

        // 🔴 `hops` の既定 2・上限 3 は計画の確定事項である（11_mcp-server-integration §6）。
        // **上限超過は丸めずエラーで拒否する** —— 黙って丸めると、呼び出し側の LLM は
        // 指定したホップ数まで探索した結果だと信じて「その先には何もない」と誤読する。
        // 値は `GraphTraversal` の定数から書き出す（2 か所に数字を持たない）。
        // 生文字列を補間しない（`}}}` が続く箇所があり補間の閉じ括弧と読み分けられない）。
        var traverseSchema = string.Concat(
            """{"type":"object","properties":{"document_id":{"type":"string","format":"uuid"},"hops":{"type":"integer","minimum":1,"maximum":""",
            GraphTraversal.MaxHops.ToString(CultureInfo.InvariantCulture),
            ""","default":""",
            GraphTraversal.DefaultHops.ToString(CultureInfo.InvariantCulture),
            """},"edge_types":{"type":"array","items":{"type":"string","format":"uuid"}}},"required":["document_id"]}""");

        return
        [
            new McpToolCandidate(Organization, new McpToolDeclaration(
                "graph.get_backlinks",
                "指定した文書を参照している文書（被参照）の一覧を返す。"
                + "ある文書がどこから引かれているかを辿るときに呼ぶ。",
                """{"type":"object","properties":{"document_id":{"type":"string","format":"uuid"}},"required":["document_id"]}""",
                $"{basePath}/get_backlinks",
                "graph:read",
                EgressClass)),
            new McpToolCandidate(Organization, new McpToolDeclaration(
                "graph.get_links",
                "指定した文書が参照している文書（参照先）の一覧を返す。"
                + "ある文書が何を引いているかを辿るときに呼ぶ。",
                """{"type":"object","properties":{"document_id":{"type":"string","format":"uuid"}},"required":["document_id"]}""",
                $"{basePath}/get_links",
                "graph:read",
                EgressClass)),
            new McpToolCandidate(Organization, new McpToolDeclaration(
                "graph.traverse",
                "指定した文書の近傍をホップ探索し、到達できた文書と辺を返す。"
                + $"hops は既定 {GraphTraversal.DefaultHops}・上限 {GraphTraversal.MaxHops} で、"
                + "上限を超える指定は丸めずエラーになる。",
                traverseSchema,
                $"{basePath}/traverse",
                "graph:read",
                EgressClass)),
        ];
    }

    // 🔴 FR-19, SC-12, ADR-0034 決定 9: 個人資料を対象に含む候補を申告から落とす。
    //
    // **判定は集合帰属（`doc_scope == "private-note"`）である。否定（`!= "organization"`）で書かない。**
    // `doc_scope` は実データ 0 件・遡及付与しない方針（ADR-0054 §結果）であり、否定で書くと
    // スコープを持たない候補がすべて個人資料に倒れて**組織向けツールが一斉に落ちる**。
    // 判定は既存の `GraphDocumentScope.IsPrivateNote` を使う —— 向きを 2 箇所に持たない。
    //
    // **本サービスには個人資料だけを対象とする面が無い**ため、現在の候補はすべて残る。
    // それでも**除外はここで通す** —— 候補が増えたときに入れ忘れが起こらないのは、
    // 「候補 → 申告」の経路が 1 本しかないからである。
    public static IReadOnlyList<McpToolDeclaration> Publishable(IEnumerable<McpToolCandidate> candidates)
        => [.. candidates.Where(c => !GraphDocumentScope.IsPrivateNote(c.Coverage)).Select(c => c.Declaration)];

    public static string SelfBaseUrl(IConfiguration configuration)
        => configuration[SelfBaseUrlKey] is { Length: > 0 } url ? url : DefaultSelfBaseUrl;

    public static ServiceToolDeclarations Declare(IConfiguration configuration)
        => new(ServiceName, Publishable(Candidates(SelfBaseUrl(configuration))));
}
