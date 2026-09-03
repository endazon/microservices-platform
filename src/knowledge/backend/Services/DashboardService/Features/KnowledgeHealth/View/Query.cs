namespace DashboardService.Features.KnowledgeHealth.View;

// FR-10, FR-17, FR-18, SC-10, ADR-0006 (#443): ナレッジ健全性指標の**出力**契約。
//
// **`Knowledge.Contracts`（サービス間契約）へは置かない。** 本指標を画面へ出すのは SC-10 の
// 「ナレッジ健全性」節であり、その節の実装は別 issue（#452 / #504）が引き受ける。
// BFF へ載せる段で契約プロジェクトへ昇格させる。契約の形はスナップショット検査
// （`check-contract-schema.js`）の対象であり、**使う側が居ない契約を先に固定しない。**
//
// **閲覧の操作だけが使う**ため、その操作のフォルダに置く（ADR-0068 決定 2）。

// 指標 1 つ分の件数。**件数のみ**（計画: 個々の文書名を出さない）。
//
// `ThresholdDays` — その指標の**現在のしきい値**（日数）。持たない指標では null。
//   planning#494 決定 3「SC-10 には件数と**現在のしきい値**を併記する」を画面が読める形にする
//   （[[IADR-0357]] 決定 4）。**件数だけを出すと、同じ数字でも意味が配備ごとに違ってしまう。**
public record KnowledgeHealthIndicatorDto(string Indicator, int Count, int? ThresholdDays = null);

// ナレッジ健全性の集計結果。
//   ObservedAt — 最も新しい観測時刻（1 件も無ければ null）。指標が古びていないかの判断材料。
//   Indicators — 7 指標すべて（0 件も欠落させない）。
public record KnowledgeHealthDto(
    DateTimeOffset? ObservedAt,
    IReadOnlyList<KnowledgeHealthIndicatorDto> Indicators);
