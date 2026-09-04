namespace DashboardService.Features.Dashboard;

// FR-10, UC-05, SC-10, ADR-0072 決定 3, [[IADR-0367]] (#1198):
// 利用イベントの**保持期間の実施**（定期削除）の構成。
//
// 🔴 **保持日数は構成キーを持たない。** `ADR-0072` §残るもの 末尾は
// 「集計の上限を変えるときは保持期間も同時に見直す（**片方だけ動かすと、照会できるのに行が
// 無い期間が生じる**）」と定めている。保持日数を独立の構成キーにすると、**その事故を運用時に
// 起こせる形をわざわざ作る**ことになる。保持日数は `DashboardEndpoints.MaxDays`（集計の上限）
// そのものであり、**削除の基準時刻も集計と同じ式**（`DashboardEndpoints.SinceUtc(MaxDays)`）から得る。
//
// **構成で変更できるのは掃除の有無と間隔だけ**である（`appsettings.json` の `UsageRetention` 節、
// または環境変数 `UsageRetention__Enabled` / `UsageRetention__IntervalMinutes`）。
public sealed class UsageRetentionOptions
{
    public const string SectionName = "UsageRetention";

    // 保持日数（＝集計の上限期間。ADR-0072 決定 3）。**定数を別に置かない** ——
    // 置いた瞬間に片方だけ動かせるようになる。
    public const int RetentionDays = DashboardEndpoints.MaxDays;

    // 掃除の間隔の既定。6 時間 —— 保持期間は 90 日であり、**1 日の中のどこで消えるかは
    // 統制の意味を変えない**。短くするほど DB を無駄に叩き、長くすると再起動の多い環境で
    // 1 周も回らないまま終わり得る。
    public const int DefaultIntervalMinutes = 360;

    // **テストでは無効にする**（器がホストを起こすたびに背景で削除が走ると、
    // 時刻に依存した検証が不安定になる。`NotificationOptions.MaintenanceEnabled` と同じ理由）。
    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; } = DefaultIntervalMinutes;

    // 実際に使う値。🔴 **不正値（0 以下）で起動を落とさない。**
    // `ValidateOnStart` を付けると、**掃除の間隔の打ち間違いでサービス全体が起動しない** ——
    // 利用イベントの記録（`POST /dashboard/events`）と集計まで巻き添えで止まるのは割に合わない。
    // `SearchTrendOptions`（[[IADR-0357]]）・`KnowledgeHealthOptions`（[[IADR-0353]] 決定 3）が
    // fail-open を選んだのと同じ向きで、**既定値へ倒す**。
    // **倒したことは常駐処理が起動時に警告へ出し、出す値も倒した後の値である**
    // （倒したのに構成値をそのまま出すと、ログと実際の周期が食い違う）。
    public int EffectiveIntervalMinutes
        => IntervalMinutes > 0 ? IntervalMinutes : DefaultIntervalMinutes;

    public bool HasInvalidInterval => IntervalMinutes <= 0;

    public TimeSpan EffectiveInterval => TimeSpan.FromMinutes(EffectiveIntervalMinutes);
}
