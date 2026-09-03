namespace GraphService.Features.KnowledgeHealth.Report;

// FR-10, UC-05, SC-10, ADR-0006, planning#494 決定 1・3, [[IADR-0353]] (#1186):
// ナレッジ健全性の**運用パラメータ**。
//
// 🔴 **SC-09（タグ辞書の実行時管理）には載せない**（planning#494 決定 3）。
// SC-09 が扱うのは**ドメインの語彙**であり、観測のしきい値は**運用パラメータ**である。
// 画面から変えられるようにすると、指標の意味が利用者操作で動き、時系列の比較が成り立たなくなる。
//
// **配備時の構成で変更できる**（`appsettings.json` の `KnowledgeHealth` 節、または環境変数
// `KnowledgeHealth__StaleDocumentThresholdDays`）。Helm では `services.graph.extraEnv` へ置く。
public sealed class KnowledgeHealthOptions
{
    public const string SectionName = "KnowledgeHealth";

    // planning#494 決定 1: **180 日**。90 日では通常の業務文書が半期を待たずに該当し、指標が
    // 「棚卸しが要る量」ではなく**文書の総量**に近づく。365 日では検知が年 1 回の粒度になり遅い。
    // **初期値であり、運用開始後の実測で改める。**
    public const int DefaultStaleDocumentThresholdDays = 180;

    // 陳腐化と見なすまでの日数。**本文**が最後に更新されてからの経過で測る。
    public int StaleDocumentThresholdDays { get; set; } = DefaultStaleDocumentThresholdDays;

    // 実際に使う値。🔴 **不正値（0 以下）で起動を落とさない。**
    //
    // `ValidateOnStart` は本サービスの `DocumentUpdated` / `DocumentDeleted` 購読ごと落とす。
    // `HttpKnowledgeHealthReporter` が fail-open を選んだ理由（「**指標の送出失敗で購読を止めない**」）と
    // 同じ向きで、**既定値へ倒す**。倒したことは収集側が警告ログに出し、
    // **報告に添えるしきい値も実際に使った値になる** —— 画面へ嘘の数字を出さないことが要点である。
    public int EffectiveStaleDocumentThresholdDays
        => StaleDocumentThresholdDays > 0
            ? StaleDocumentThresholdDays
            : DefaultStaleDocumentThresholdDays;

    public bool HasInvalidStaleDocumentThreshold => StaleDocumentThresholdDays <= 0;
}
