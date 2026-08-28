using System.Text.Json.Serialization;
using DocumentService.Domain;

namespace DocumentService.Features.McpTools;

// FR-16, ADR-0024 §2, [[IADR-0292]]: `GET /internal/mcp-tools` が返す自己申告の形。
//
// 🔴 **契約は新設していない。** McpServer の `Domain/McpToolContracts.cs`（`ServiceToolDeclarations` /
// `McpToolDeclaration`）の**ワイヤ形式にそのまま合わせた写し**である。共有化しない理由は 2 つある。
//
//   1. **可変ユニットから platform の McpServer は参照できない**（ユニット外参照は
//      `platform/backend/Shared/` の 3 プロジェクトのみ）。GraphService の `GraphDocumentScope` が
//      McpServer の `DocumentScope` を共有せず持っているのと同じ制約である（IADR-0274 §検討した選択肢）。
//   2. **`Platform.Shared.Contracts` への昇格（IADR-0269 決定 6）は本 issue の領域外**である ——
//      `*.Contracts` への型追加は `scripts/contract-schema-baseline.json` の更新を伴う。
//      昇格は追随 issue で行う（[[IADR-0292]] 決定 3）。
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
//
// 🔴 **候補を素通しで申告しない。** 個人資料（`private-note`）を対象に含む候補は申告から落とす。
// 計画は「探索系に限らず検索系（`retrieval.*`）・文書取得系（`document.*`）にも同様に適用する。
// 経路によって扱いが変わると、除外の意味が失われる」と定めている（11_mcp-server-integration §6）。
public sealed record McpToolCandidate(
    IReadOnlyDictionary<string, string> Coverage,
    McpToolDeclaration Declaration);

// FR-16, ADR-0024 §2: DocumentService の自己申告。
//
// 公開範囲は ADR-0024 §決定「初期公開範囲」の**文書取得系（`document.*`）**である。
public static class McpToolDeclarationSource
{
    // FR-15 の `/internal/introspection` と同じサービス名を使う（同じ規約系に置くため）。
    public const string ServiceName = "document-service";

    // 申告する実行口の基底 URL。メッシュ内の自サービス URL を構成で上書きできる。
    public const string SelfBaseUrlKey = "Mcp:SelfBaseUrl";
    public const string DefaultSelfBaseUrl = "http://document-service:8080";

    // ADR-0024 §5「egress_class 必須」。欠けた申告は McpServer が公開しない（安全側）。
    private const string EgressClass = "internal";

    // 組織文書だけを対象とする候補の被覆。**空辞書ではなく明示の `organization` を置く** ——
    // 「スコープを書き忘れた候補」と「組織文書を対象とする候補」を読み分けられるようにするため。
    private static IReadOnlyDictionary<string, string> Organization { get; } =
        new Dictionary<string, string> { [DocumentAttributes.DocScopeKey] = DocumentAttributes.DocScopeOrganization };

    private static IReadOnlyDictionary<string, string> PrivateNote { get; } =
        new Dictionary<string, string> { [DocumentAttributes.DocScopeKey] = DocumentAttributes.DocScopePrivateNote };

    // 申告し得る候補の全体。**除外は下の Publishable が 1 箇所で行う。**
    public static IReadOnlyList<McpToolCandidate> Candidates(string selfBaseUrl)
    {
        var basePath = selfBaseUrl.TrimEnd('/') + "/internal/mcp";
        return
        [
            new McpToolCandidate(Organization, new McpToolDeclaration(
                "document.get_document",
                "文書 ID を指定して 1 件の文書（タイトル・属性・本文の参照）を取得する。"
                + "検索結果や被参照一覧で得た document_id の中身を読むときに呼ぶ。",
                """{"type":"object","properties":{"document_id":{"type":"string","format":"uuid"}},"required":["document_id"]}""",
                $"{basePath}/get_document",
                "document:read",
                EgressClass)),
            new McpToolCandidate(Organization, new McpToolDeclaration(
                "document.list_documents",
                "更新の新しい順に文書の一覧（ID・タイトル・属性）を返す。"
                + "何があるかを俯瞰したいとき、または検索語が決まっていないときに呼ぶ。",
                """{"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":100,"default":20}}}""",
                $"{basePath}/list_documents",
                "document:read",
                EgressClass)),

            // 🔴 SC-12, ADR-0024（2026-08-02 注記）, ADR-0034 決定 9: **申告しない候補。**
            // `/private-notes`（FR-19 / SC-19）は実在する面であり、文書取得系のツールとして
            // 最も自然に挙がる候補である。だからこそ**候補として書き、除外を目に見える形で持つ** ——
            // 「思い付かなかったから無い」と「規則で落としている」は、一覧に現れない点では同じに見える。
            new McpToolCandidate(PrivateNote, new McpToolDeclaration(
                "document.list_private_notes",
                "所有者の個人資料（private-note）を一覧する。",
                """{"type":"object","properties":{"owner":{"type":"string"}},"required":["owner"]}""",
                $"{basePath}/list_private_notes",
                "document:read",
                EgressClass)),
        ];
    }

    // 🔴 FR-19, SC-12, ADR-0034 決定 9: 個人資料を対象に含む候補を申告から落とす。
    //
    // **判定は集合帰属（`doc_scope == "private-note"`）である。否定（`!= "organization"`）で書かない。**
    // `doc_scope` は実データ 0 件・既存文書へ遡及付与しない方針（ADR-0054 §結果）であり、
    // 否定で書くとスコープを持たない候補がすべて個人資料に倒れて**組織向けツールが一斉に落ちる**。
    // 判定そのものは `DocumentAttributes.IsPrivateNote` を使う —— 向きを 2 箇所に持つと片方だけが裏返る。
    //
    // **2 つの書き方は「個人資料を除外する」点では動作で見分けがつかない。**
    // 分けられるのは陽性対照（組織スコープ・スコープ無しの候補が落ちないこと）だけである。
    public static IReadOnlyList<McpToolDeclaration> Publishable(IEnumerable<McpToolCandidate> candidates)
        => [.. candidates.Where(c => !DocumentAttributes.IsPrivateNote(c.Coverage)).Select(c => c.Declaration)];

    public static string SelfBaseUrl(IConfiguration configuration)
        => configuration[SelfBaseUrlKey] is { Length: > 0 } url ? url : DefaultSelfBaseUrl;

    public static ServiceToolDeclarations Declare(IConfiguration configuration)
        => new(ServiceName, Publishable(Candidates(SelfBaseUrl(configuration))));
}
