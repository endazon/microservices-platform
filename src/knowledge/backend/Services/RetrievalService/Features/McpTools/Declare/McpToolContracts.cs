using System.Text.Json.Serialization;
using Knowledge.Contracts.Dtos;

namespace RetrievalService.Features.McpTools.Declare;

// ADR-0068 決定 2, [[IADR-0319]]: 本ファイルを使う操作は `McpTools/Declare` の 1 つだけなので 3 段目に置く。
// **「申告の語彙だから操作をまたぐ」ではない** —— 判定は所属（どの操作が使うか）であって、内容の抽象度ではない。

// FR-16, ADR-0024 §2, [[IADR-0292]]: `GET /internal/mcp-tools` が返す自己申告の形。
//
// 🔴 **契約は新設していない。** McpServer の `Domain/McpToolContracts.cs` の**ワイヤ形式に
// そのまま合わせた写し**である。共有化しない理由（ユニット外参照の制約と、
// `Platform.Shared.Contracts` への昇格が本 issue の領域外であること）は [[IADR-0292]] 決定 3。
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

// FR-16, ADR-0024 §2: RetrievalService の自己申告。
//
// 公開範囲は ADR-0024 §決定「初期公開範囲」の**検索系（`retrieval.*`）**である。
// ツール名は計画（11_mcp-server-integration §2）が例示した `retrieval.search_documents` をそのまま使う。
public static class McpToolDeclarationSource
{
    // FR-15 の `/internal/introspection` と同じサービス名を使う（同じ規約系に置くため）。
    public const string ServiceName = "retrieval-service";

    public const string SelfBaseUrlKey = "Mcp:SelfBaseUrl";
    public const string DefaultSelfBaseUrl = "http://retrieval-service:8080";

    // ADR-0024 §5「egress_class 必須」。欠けた申告は McpServer が公開しない（安全側）。
    private const string EgressClass = "internal";

    private static IReadOnlyDictionary<string, string> Organization { get; } =
        new Dictionary<string, string> { [DocumentScopes.Key] = DocumentScopes.Organization };

    // 申告し得る候補の全体。**除外は下の Publishable が 1 箇所で行う。**
    //
    // 🔴 **索引には個人資料が載り得る**（所有者が「横断検索に含める」を ON にしたもの。
    // FR-21 受け入れ基準 ⑨ / [[IADR-0283]] 決定 3）。したがって MCP へ出す検索ツールは
    // **組織文書に限る**ものとして申告する —— サービスアカウント実行では個人資料を一律に
    // 対象外とする（ADR-0034 決定 9。検索系にも適用される）。
    public static IReadOnlyList<McpToolCandidate> Candidates(string selfBaseUrl)
    {
        var basePath = selfBaseUrl.TrimEnd('/') + "/internal/mcp";
        return
        [
            new McpToolCandidate(Organization, new McpToolDeclaration(
                "retrieval.search_documents",
                "自然文のクエリで社内ナレッジを横断検索し、関連する文書とその抜粋を返す。"
                + "答えの根拠になりそうな文書を探すときに最初に呼ぶ。",
                """{"type":"object","properties":{"query":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":50,"default":10}},"required":["query"]}""",
                $"{basePath}/search_documents",
                "retrieval:search",
                EgressClass)),
        ];
    }

    // 🔴 FR-19, SC-12, ADR-0034 決定 9: 個人資料を対象に含む候補を申告から落とす。
    //
    // **判定は集合帰属（`doc_scope == "private-note"`）である。否定（`!= "organization"`）で書かない。**
    // `doc_scope` は実データ 0 件・遡及付与しない方針（ADR-0054 §結果）であり、否定で書くと
    // スコープを持たない候補がすべて個人資料に倒れて**組織向けツールが一斉に落ちる**。
    // 判定は `DocumentScopes.IsPrivateNote`（ユニット共通の語彙）を使う。
    public static IReadOnlyList<McpToolDeclaration> Publishable(IEnumerable<McpToolCandidate> candidates)
        => [.. candidates.Where(c => !DocumentScopes.IsPrivateNote(c.Coverage)).Select(c => c.Declaration)];

    public static string SelfBaseUrl(IConfiguration configuration)
        => configuration[SelfBaseUrlKey] is { Length: > 0 } url ? url : DefaultSelfBaseUrl;

    public static ServiceToolDeclarations Declare(IConfiguration configuration)
        => new(ServiceName, Publishable(Candidates(SelfBaseUrl(configuration))));
}
