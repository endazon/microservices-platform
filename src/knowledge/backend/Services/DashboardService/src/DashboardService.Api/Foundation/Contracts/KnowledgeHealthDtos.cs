namespace DashboardService.Api.Foundation.Contracts;

// FR-10, FR-17, FR-18, SC-10, ADR-0006 (#443): ナレッジ健全性指標の入出力契約。
//
// **`Knowledge.Contracts`（サービス間契約）へは置かない。** 本指標を画面へ出すのは SC-10 の
// 「ナレッジ健全性」節であり、その節の実装は別 issue（#452 / #504）が引き受ける。
// BFF へ載せる段で契約プロジェクトへ昇格させる。契約の形はスナップショット検査
// （`check-contract-schema.js`）の対象であり、**使う側が居ない契約を先に固定しない。**

// 観測値 1 件の報告。
//   SubjectKey — 重複排除のための不透明な鍵（文書 ID・辺の型名など）。**応答には現れない。**
//   DocScope   — 文書スコープ（`private-note` は集計から除外される）。持たない対象は null。
public record KnowledgeHealthObservationRequest(string SubjectKey, string? DocScope = null);

// 指標 1 つ分の観測値の**スナップショット置換**。
// 差分ではなく全量で送る —— 「解消した観測値」を取り消す経路を別に持つと、
// 取り消し漏れが件数を恒久的に膨らませる。
public record KnowledgeHealthReportRequest(
    string Indicator,
    IReadOnlyList<KnowledgeHealthObservationRequest> Observations);

// 指標 1 つ分の件数。**件数のみ**（計画: 個々の文書名を出さない）。
public record KnowledgeHealthIndicatorDto(string Indicator, int Count);

// ナレッジ健全性の集計結果。
//   ObservedAt — 最も新しい観測時刻（1 件も無ければ null）。指標が古びていないかの判断材料。
//   Indicators — 7 指標すべて（0 件も欠落させない）。
public record KnowledgeHealthDto(
    DateTimeOffset? ObservedAt,
    IReadOnlyList<KnowledgeHealthIndicatorDto> Indicators);
