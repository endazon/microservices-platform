namespace Knowledge.Contracts.Dtos;

// FR-17, SC-09, SC-10, ADR-0033 決定 3・9, IADR-0242 決定 7・8: 辺の型辞書の契約。
//
// ADR-0033 決定 9 が定めた規則（参照が 1 件でもある型は削除拒否・改名は許し既存の辺は追随・
// 削除前に使用件数を示す）は**すべて契約側の機能**であり、辞書が無いと 1 つも満たせない。
// タグ辞書（`TagDictionaryDto`）と**同じ契約**だが、**実装は共有しない** ——
// 数え方が根本的に違うためである（下記）。

// FR-17, SC-09, SC-10: 使用件数つきの 1 件。
//
// `UsageCount` は **その型を参照している辺の数**である。
//
// **タグ辞書とは数え方が違う。** タグは `Document.Tags`（jsonb の `List<Guid>`）に埋まっており
// SQL で展開して数えられないため、全文書をメモリへ読み出して集計している。
// 辺の型は `edges.edge_type_id` が**実カラム＋外部キー**なので `COUNT(*)` 一発で、
// `ix_edges_type` が効く。**契約は移植するが、コードは移植しない。**
//
// **版の論点は無い。** 辺は最新版のみ保持する（ADR-0033 決定 6）ので、
// タグ辞書が抱えた「版履歴を数えると永久に削除できなくなる」問題は起きない。
public record EdgeTypeDto(
    Guid Id,
    string Name,
    string Layer,
    bool IsSymmetric,
    bool IsSeed,
    int UsageCount);

// FR-17, SC-18 (#962): **描画用**の 1 件。SC-18（一般利用者のグラフビュー）が辺の型名・層・
// 対称性を解決するために使う。
//
// 🔴 **`UsageCount` を持たない。これは省略ではなく、意図した除外である。**
// 使用件数は `db.Edges.CountAsync(...)` で**全辺を数えて**おり ABAC で絞られていない
// （管理者向けの `EdgeTypeDto` では設計どおり）。一般利用者へ返すと、ホップごと ABAC
// （IADR-0242）が個々のノード・辺を隠しているのに**集計値が総量を漏らす**。
// 隠すのは集計値であって語彙ではない —— 型名そのものはタグ辞書と同じ「語彙」であり、
// これを絞る理由は無い。
public record EdgeTypeCatalogItemDto(
    Guid Id,
    string Name,
    string Layer,
    bool IsSymmetric);

// FR-17, SC-09: 辞書へ型を追加する。
// **識別子は辞書が採番する**（呼び出し側から与えない。改名で変わらない値を外から決めさせない）。
public record CreateEdgeTypeRequest(string Name, string Layer, bool IsSymmetric);

// FR-17, SC-09, ADR-0033 決定 9: 型を改名する。
//
// **識別子は変えない。** 辺は識別子を参照しているので追随は自動であり、
// **辺の行は 1 行も書き換わらない**（IADR-0242 決定 7）。
public record RenameEdgeTypeRequest(string Name);

// FR-17, SC-09: 参照がある型の削除を拒むときの応答。
//
// **件数を添える** —— ADR-0033 決定 9 が「削除前に使用件数を示す」と定めており、
// 数だけでも「まず 3 本の辺を付け替えてから消す」と管理者が行動できる。
public record EdgeTypeInUseResponse(string Error, string Message, int UsageCount);
