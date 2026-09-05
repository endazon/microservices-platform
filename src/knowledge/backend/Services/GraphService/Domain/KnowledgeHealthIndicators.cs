namespace GraphService.Domain;

// FR-10, FR-17, SC-10, [[IADR-0299]] (#443): 本サービスが生産する健全性指標の名前。
//
// 🔴 **語彙の正本は受け口（DashboardService.Domain.KnowledgeHealthIndicators）である。**
// ここに持つのは**本サービスが実際に送る指標だけ**であり、7 指標の一覧を複写しない
// （複写すると、片方だけが増えたときに「綴りは合っているのに受け口が 400 を返す」形の
// 乖離が生まれる。受け口は値域を閉じており、未知の名前は 400 で落ちる）。
//
// **サービスを跨ぐため定数を共有できない**（サービス間は直接参照しない）。
// `/internal/notifications` の送信側・受け口と同じく、**文字列の一致はテストで固定する**。
internal static class KnowledgeHealthIndicators
{
    // 孤立文書数: どの文書からも参照されず、どの文書も参照していない文書。
    public const string OrphanDocuments = "orphan-documents";

    // FR-10, UC-05, SC-10, planning#494, [[IADR-0353]] (#1186):
    // 陳腐化文書数: **本文**が一定期間更新されていない文書。
    // 🔴 **起点は本文の更新のみである**（タグ・属性の更新は起点にしない）。
    // 判定は GraphDocument.BodyUpdatedAt、しきい値は KnowledgeHealthOptions（既定 180 日）。
    public const string StaleDocuments = "stale-documents";

    // FR-10, FR-17, UC-05, SC-10, [[IADR-0389]] (#1246):
    // 解決できないリンク数。**リンク先の名前から文書 ID を特定できない**リンク。
    // 🔴 不在（相手が無い）と曖昧（同名が複数）の**両方**を含む —— どちらも辺が作られず、
    // 利用者から見れば同じ「繋がっていないリンク」である。内訳の軸で理由を分ける。
    public const string UnresolvedLinks = "unresolved-links";

    // FR-17, SC-10, ADR-0033 決定 9, [[IADR-0389]] (#1246):
    // 辺の型ごとの使用件数。**内訳の軸に型名を載せる**（軸は実行時辞書 `edge_types` の語彙で有界）。
    public const string EdgeTypeUsage = "edge-type-usage";
}
