using System.Text.Json.Serialization;

namespace McpServer.Domain;

// FR-16, ADR-0024: 各サービスが `GET /internal/mcp-tools` で自己申告するツール定義の規約。
//
// 計画（06_technical/11_mcp-server-integration §2「ツール定義規約」）が挙げる 6 項目
// （name / description / input_schema / endpoint / required_scope / egress_class）をそのまま持つ。
// 詳細形は計画が実装リポジトリへ明示的に委任している（ADR-0024 §結果）。
//
// 🔴 **本 DTO を Platform.Shared.Contracts へ置いていない**のは、起草時点で**プロセス内の生成側が
// 1 つも存在しなかった**ためである（`/internal/mcp-tools` はどのサービスにも未実装。実測 0 件）。
// MCP サーバーは HTTP で受け取って解釈する側だけであり、共有契約にすると「利用者が 1 人しか
// いない契約」を全ユニットへ配ることになる。**最初の生成側が実装された時点で昇格させる**
// （判断と条件は IADR-0269 決定 6）。
//
// ［2026-08-28 追記 / #1020］🔴 **生成側は実装された** —— DocumentService / RetrievalService /
// GraphService の 3 サービスが `GET /internal/mcp-tools` を実装し、実効カタログは空でなくなった。
// **昇格の条件は満たされたが、昇格そのものは追随 issue へ回している**（`*.Contracts` への型追加は
// `scripts/contract-schema-baseline.json` の更新を伴い、#1020 の領域宣言の外だった。IADR-0292 決定 4）。
// **昇格までは本ファイルがワイヤ形式の正本である** —— 3 サービスが持つのは写しであり、
// ここを変えるときは 3 箇所を同時に追随させること。
public sealed record McpToolDeclaration(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("input_schema")] string InputSchema,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("required_scope")] string RequiredScope,
    [property: JsonPropertyName("egress_class")] string EgressClass);

// FR-16, ADR-0024: 1 サービスぶんの自己申告（`GET /internal/mcp-tools` の応答）。
public sealed record ServiceToolDeclarations(
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("tools")] IReadOnlyList<McpToolDeclaration> Tools);

// FR-16, ADR-0024: 宣言的公開構成（許可リスト）の 1 エントリ。Git 管理・GitOps 適用。
// PublishedName を省略すると申告名をそのまま公開名にする。
public sealed record ToolPublicationEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("published_name")] string? PublishedName = null);

// FR-16, ADR-0024 / ADR-0034 決定 9: 公開構成本体。
// ServiceAccountAttributes は無人アカウントへ割り当てる ABAC 属性の宣言であり、
// **`doc_scope=private-note` を含めてはならない**（検証で弾く。ToolPublicationConfigValidator）。
public sealed record ToolPublicationConfig(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("tools")] IReadOnlyList<ToolPublicationEntry> Tools,
    [property: JsonPropertyName("service_account_attributes")]
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? ServiceAccountAttributes = null);
