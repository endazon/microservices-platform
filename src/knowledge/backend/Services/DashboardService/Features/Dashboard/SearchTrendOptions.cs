namespace DashboardService.Features.Dashboard;

// FR-10, UC-05, SC-10, ADR-0071 決定 1, [[IADR-0357]] (#1197):
// 検索傾向の**秘匿パラメータ**（出現件数の下限）。
//
// 🔴 **検索語は自由文である。** 語の種類が上位件数 `top` に満たない期間では、
// **1 回しか検索されていない語がそのまま運用者へ出る**（ADR-0071 §コンテキスト）。
// 計画は「内容（タイトル・本文・検索語）は出さず件数まで」を 3 文書 4 か所で既に定めていたが、
// いずれも通知・メール・ログを射程にしており、**画面だけがその外に居た**。
// 本オプションは、その原則を画面へ届かせる線である。
//
// **配備時の構成で変更できる**（`appsettings.json` の `SearchTrend` 節、または環境変数
// `SearchTrend__MinimumCount`）。ADR-0071 決定 1 末尾「本値は配備時の構成で変更できる」。
//
// 段は `Features/Dashboard/` 直下である —— **検索傾向とサマリの 2 操作が使う**（ADR-0068 決定 2）。
public sealed class SearchTrendOptions
{
    public const string SectionName = "SearchTrend";

    // ADR-0071 決定 1: **3**。1 回は偶発であり、2 回は**同じ人が語を言い換えて引き直した**場合を
    // 多く含む（検索は 1 回で当たらなければ打ち直す操作である）。3 回目からは
    // 「1 度の探索の中での打ち直し」では説明しにくくなる。
    // **初期値であり、運用開始後の実測で改める**（ADR-0071 §残るもの）。
    public const int DefaultMinimumCount = 3;

    // 上位一覧に出す最小の出現件数。**これ未満の語は落とす**（「その他 M 件」へ集約しない ——
    // M 自体が推測の材料になる。とくに「その他 1 件」は伏せた意味がほとんど残らない）。
    public int MinimumCount { get; set; } = DefaultMinimumCount;

    // 実際に使う値。🔴 **不正値（0 以下）で起動を落とさない。**
    //
    // `ValidateOnStart` を付けると、**指標の秘匿パラメータの打ち間違いでサービス全体が起動しない**。
    // 利用イベントの記録（`POST /dashboard/events`）まで巻き添えで止まるのは割に合わない。
    // `KnowledgeHealthOptions`（[[IADR-0353]] 決定 3）が fail-open を選んだのと同じ向きで、
    // **既定値へ倒す**。倒したことは照会側が警告ログに出し、
    // **応答に添えるしきい値も実際に使った値になる** —— **画面へ嘘の数字を出さない**ことが要点である。
    // 倒したのに構成値をそのまま返すと、**見える語と表示されたしきい値が食い違う**。
    public int EffectiveMinimumCount
        => MinimumCount > 0 ? MinimumCount : DefaultMinimumCount;

    public bool HasInvalidMinimumCount => MinimumCount <= 0;
}
