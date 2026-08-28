using System.Text.Json.Serialization;

namespace McpServer.Domain;

// FR-16, UC-08 基本フロー 5, ADR-0024 §4: ツール応答の共通エンベロープ。
//
// 🔴 **MCP サーバーが文書単位で送信可否を判定しフィルタする**ことを UC-08 が定めている以上、
// 応答は「文書のリスト」として MCP サーバーから読める形でなければならない。素通しのプロキシに
// すると、統制は各サービスの実装頼みになり、**構造として担保できない**。
// 形そのものは計画が実装へ委任した範囲である（ADR-0024 §結果）。判断は IADR-0269。
public sealed record McpToolResult(
    [property: JsonPropertyName("documents")] IReadOnlyList<McpToolDocument> Documents,
    // ADR-0034 決定 4: 打ち切りの事実と件数を返す。**件数は認可判定を通したあとの件数**である。
    [property: JsonPropertyName("total_count")] int TotalCount,
    [property: JsonPropertyName("truncated")] bool Truncated = false);

// FR-16: 応答に載る 1 文書。Attributes は ABAC 基本属性（`confidentiality` / `doc_scope` 等）。
// Body は本文（越境不可なら落として ReferenceUrl のみにする。ADR-0024 §4）。
public sealed record McpToolDocument(
    [property: JsonPropertyName("document_id")] string DocumentId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("attributes")] IReadOnlyDictionary<string, string> Attributes,
    [property: JsonPropertyName("body")] string? Body = null,
    [property: JsonPropertyName("reference_url")] string? ReferenceUrl = null);
