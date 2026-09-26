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

        var interval = configuration[IntervalKey] switch
        {
            null or "" => DefaultInterval,
            var declared when TimeSpan.TryParse(declared, System.Globalization.CultureInfo.InvariantCulture, out var t)
                             && t > TimeSpan.Zero => t,
            var declared => throw new InvalidOperationException(
                $"{IntervalKey} の値 '{declared}' は不正である（正の時間。例 01:00:00）。"),
        };

        return new DepartmentAttributeSyncOptions(mode, interval);
    }
}

public sealed class DepartmentAttributeSyncHostedService(
    IServiceScopeFactory scopeFactory,
    DepartmentAttributeSyncOptions options,
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
                logger.LogError(ex, "部門の同期で例外が発生した。次の周期で再試行する。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
