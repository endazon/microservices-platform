namespace DashboardService.Features.KnowledgeHealth.Report;

// FR-10, FR-17, FR-18, SC-10, ADR-0006 (#443): ナレッジ健全性指標の**入力**契約。
//
// **`Knowledge.Contracts`（サービス間契約）へは置かない。** 生産者は同ユニットの定期処理であり、
// 契約プロジェクトへの昇格は BFF へ載せる段で行う（`KnowledgeHealth/View/Query.cs` と同じ理由）。
//
// **観測値の受け口だけが使う**ため、その操作のフォルダに置く（ADR-0068 決定 2）。

// 観測値 1 件の報告。
//   SubjectKey — 重複排除のための不透明な鍵（文書 ID・辺の型名など）。**応答には現れない。**
//   DocScope   — 文書スコープ（`private-note` は集計から除外される）。持たない対象は null。
//   Dimension  — ★［2026-09-05 / #1246・[[IADR-0389]] 決定 1］**内訳の軸**。
//                閲覧は指標の件数に加えて軸ごとの内訳を返す（辺の型ごとの使用件数など）。
//                🔴 **基数が有界な語だけを載せる**（自由語を入れると内訳が無界に増える）。
//                **省略可**（軸を持たない指標では現れない）。
public record KnowledgeHealthObservationRequest(
    string SubjectKey, string? DocScope = null, string? Dimension = null);

// 指標 1 つ分の観測値の**スナップショット置換**。
// 差分ではなく全量で送る —— 「解消した観測値」を取り消す経路を別に持つと、
// 取り消し漏れが件数を恒久的に膨らませる。
//
// `ThresholdDays` — 生産者が判定に使った日数のしきい値（planning#494 決定 3 / [[IADR-0353]] 決定 4）。
//   🔴 **観測値ではなく報告 1 通の属性である** —— 件数が 0 のとき観測値は 1 行も無く、
//   観測値へ乗せるとしきい値も一緒に消える（0 件こそ表示したい状態である）。
//   **省略可**（しきい値を持たない指標では現れない）。**0 以下は 400** で落とす ——
//   意味を持たない値を保存すると、画面が「しきい値 0 日」と表示してしまう。
public record KnowledgeHealthReportRequest(
    string Indicator,
    IReadOnlyList<KnowledgeHealthObservationRequest> Observations,
    int? ThresholdDays = null);
