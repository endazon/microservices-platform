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
    bool IsSymmetric,
    // FR-04, FR-17, ADR-0035 決定 2 (#970): 二段検索の再ランクが使う**辺の型ごとの重み**。
    // RetrievalService が利用者の資格情報（方式 A の伝播）で引ける口はカタログだけなので、
    // ここに載せる。重みは型ごとの語彙レベルの設定値であり、`UsageCount` と違って
    // ABAC で絞るべき集計値ではない（権限外文書の存在を漏らす経路にならない）。
    //
    // 🔴 **既定値つきで足す**（既定値の無いメンバー追加は契約上の破壊的変更。IADR-0122 決定 2）。
    // 既定 0.5 は GraphService の `EdgeType.DefaultWeight` と同値だが、契約プロジェクトは
    // サービス実装を参照できないためリテラルで持つ。
    double Weight = 0.5);

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

// FR-18, SC-21, ADR-0033 決定 7・10: AI 提案（#914）。
//
// **リンク提案とタグ提案を 1 つの型で表す。** SC-21 が同一一覧を求めており（画面を分けると
// 片方が忘れられる）、DTO を分けるとクライアント側で 2 経路に割れる。種別固有の欄は null 可。
//
// 🔴 **本文指紋を公開面へ出さない。** 指紋は却下解除の判定に使う内部状態であり、
// 利用者に見せる意味が無い。出すと「文書の内容が変わったか」を、文書を読めない利用者にも
// 判定させる副次経路になり得る。
public record AiSuggestionDto(
    Guid Id,
    string Kind,
    Guid SourceDocumentId,
    Guid? TargetDocumentId,
    Guid? EdgeTypeId,
    string? TagValue,
    string Rationale,
    string State,
    // ADR-0033 §結果 フォローアップ: SC-10 が「3 回以上却下された件数」を観測する。
    int RejectedCount,
    // SC-21: 再提示には**理由を必ず添える**。"source" / "target" / "both" / null。
    string? ReinstatedReason,
    // FR-18, SC-21 (#918): 一覧が描く**両端の文書名**。SC-21 主要素 1 の「提案の内容」列は
    // 「両端の文書名・辺の型、またはタグ名」であり、ID だけでは描けない。
    //
    // 🔴 **既定値つきで足す**（既定値の無いメンバー追加は契約上の破壊的変更。IADR-0122 決定 2）。
    //
    // 🔴 **辺の型名はここへ入れない。** 表示名は辞書（`/graph/edge-types/catalog`）で解決し、
    // 改名に追随させる（ADR-0033 決定 9）。DTO へ焼き込むと改名後も古い名前を出し続ける。
    //
    // 可視性は一覧側が既に両端の複製を読んで判定しているため、**照会は 1 件も増えない**。
    // タグ提案は終点を持たないので `TargetDocumentTitle` は null である。
    string SourceDocumentTitle = "",
    string? TargetDocumentTitle = null);

// FR-18, ADR-0033 決定 10: 却下。**両端の本文指紋を添える**（解除の判定に使う）。
//
// 指紋は不透明な文字列であり、作り方は本サービスの関心ではない。**現状 `DocumentUpdated` は
// 本文指紋を持たない**ため、供給元は #911 が決める（#914 仕様書 §未決事項）。
public record RejectAiSuggestionRequest(string? SourceFingerprint, string? TargetFingerprint);
