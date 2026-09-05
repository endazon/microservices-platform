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
//   （[[IADR-0353]] 決定 4）。**件数だけを出すと、同じ数字でも意味が配備ごとに違ってしまう。**
public record KnowledgeHealthIndicatorDto(
    string Indicator, int Count, int? ThresholdDays = null,
    IReadOnlyList<KnowledgeHealthDimensionDto>? Breakdown = null);

// ★［2026-09-05 / #1246・[[IADR-0389]] 決定 1］指標の**内訳** 1 件（軸の名前と件数）。
//
// IADR-0265 が先送りしていた「指標 1 つ＝件数 1 つ」を解く。辺の型ごとの使用件数
// （ADR-0033 決定 9）は内訳が無ければ**合計しか出せず、どの型が使われているか分からない**。
//
// 🔴 **`Breakdown` の `null` と空リストは意味が違う。**
//   null — その指標は軸を持たない（内訳という概念が無い / 観測値が 1 件も無い）。
//   []   — 軸を持つが、除外後に残った観測値が 0 件。
// 画面が「内訳が空」と「内訳が無い」を区別できるようにする（0 件と欠落を混同させない
// という本 DTO 全体の姿勢と同じ）。
//
// 内訳の合計は `Count` と一致する（**同じ除外規則を通した後の値**を畳んでいる）。
public record KnowledgeHealthDimensionDto(string Dimension, int Count);

// ナレッジ健全性の集計結果。
//   ObservedAt — 最も新しい観測時刻（1 件も無ければ null）。指標が古びていないかの判断材料。
//   Indicators — 7 指標すべて（0 件も欠落させない）。
public record KnowledgeHealthDto(
    DateTimeOffset? ObservedAt,
    IReadOnlyList<KnowledgeHealthIndicatorDto> Indicators);
