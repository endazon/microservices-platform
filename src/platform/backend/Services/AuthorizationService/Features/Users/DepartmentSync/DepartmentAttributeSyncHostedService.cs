namespace AuthorizationService.Features.Users.DepartmentSync;

// FR-05, FR-09, SC-17, 計画 ADR-0115 決定 3, [[IADR-0473]] (#1573): 部門の同期を定期的に回す器。
//
// 🔴 **opt-in である。** `DepartmentAttributeSync:Mode` が未設定（＝ Off）なら 1 回も回さず、IdP へ問い合わせもしない。
// 値域外の宣言は起動時に落とす（`IdentityAdmin:Provider` / `RetentionAnchor:Source` と同じ deny-by-default ——
// 打ち間違いを「無効」へ黙って倒すと、有効にしたつもりで何も起きない）。
public sealed record DepartmentAttributeSyncOptions(DepartmentAttributeSyncMode Mode, TimeSpan Interval)
{
    public const string ModeKey = "DepartmentAttributeSync:Mode";
    public const string IntervalKey = "DepartmentAttributeSync:Interval";

    // 既定の周期。人事連携がグループを変えてから属性が追随するまでの遅れの上限になる。
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);

    // 周期の下限。これより短いと Keycloak を叩きすぎる（1 周で部門グループの数だけ往復が走る）。
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    public static DepartmentAttributeSyncOptions FromConfiguration(IConfiguration configuration)
    {
        var declaredMode = configuration[ModeKey];
        var mode = declaredMode?.Trim().ToLowerInvariant() switch
        {
            null or "" or "off" => DepartmentAttributeSyncMode.Off,
            "report" => DepartmentAttributeSyncMode.Report,
            "fix" => DepartmentAttributeSyncMode.Fix,
            _ => throw new InvalidOperationException(
                $"{ModeKey} の値 '{declaredMode}' は不正である（Off / Report / Fix のいずれか）。未設定なら Off。"),
        };

        // ［2026-09-26 / #1573 監査］🔴 **書式は `hh:mm:ss` だけを受け付け、1 分以上 23:59:59 以下。**
        // `TimeSpan.TryParse("60")` は **60 日**を、`TryParse("24:00:00")` は **24 日**を返す（時が 24 以上だと日として読む）。
        // さらに 50 日超は `PeriodicTimer` が起動後に例外を投げる。**`TryParseExact` の `hh` は 0〜23 しか受けない**ので、
        // 誤読も上限超過も起動時に落ちる。下限が無いと `00:00:01` で Keycloak を毎秒叩ける。
        var declaredInterval = configuration[IntervalKey];
        TimeSpan interval;
        if (string.IsNullOrWhiteSpace(declaredInterval))
            interval = DefaultInterval;
        else if (TimeSpan.TryParseExact(declaredInterval.Trim(), @"hh\:mm\:ss",
                     System.Globalization.CultureInfo.InvariantCulture, out var t)
                 && t >= MinimumInterval)
            interval = t;
        else
            throw new InvalidOperationException(
                $"{IntervalKey} の値 '{declaredInterval}' は不正である（hh:mm:ss 形式・00:01:00〜23:59:59。例 01:00:00）。");

        return new DepartmentAttributeSyncOptions(mode, interval);
    }
}

public sealed class DepartmentAttributeSyncHostedService(
    IServiceScopeFactory scopeFactory,
    DepartmentAttributeSyncOptions options,
    DepartmentAttributeSyncMetrics metrics,
    ILogger<DepartmentAttributeSyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Mode == DepartmentAttributeSyncMode.Off)
        {
            logger.LogInformation(
                "部門の同期は無効です（{Key} 未設定 = Off）。利用者属性 department を部門グループへ合わせるには Report / Fix を設定する。",
                DepartmentAttributeSyncOptions.ModeKey);
            return;
        }

        using var timer = new PeriodicTimer(options.Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<DepartmentAttributeSync>()
                    .RunAsync(options.Mode, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 1 周の失敗で常駐を止めない（IdP の一時障害で以後の同期が静かに止まるのを避ける）。
                // ［2026-09-26 / #1573 監査］**周期ごと失敗したことも計器に出す**（`aborted`）。数えないと、
                // 木の読み取り（部門グループ・所属者）が毎周期落ちていても計器が静かなままになる。
                metrics.RecordCycle("aborted");
                logger.LogError(ex, "部門の同期で例外が発生した（周期ごと中断）。次の周期で再試行する。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
