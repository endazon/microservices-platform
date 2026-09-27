using System.Diagnostics;
using System.Diagnostics.Metrics;
using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Features.Authz.OwnerReadPolicyGuard;

// FR-05, FR-19, NFR-09, NFR-21, 計画 ADR-0121 決定 2・4・フォローアップ 2, ADR-0006, [[IADR-0481]] (#1665):
// **所有者の読み取りのポリシーが消えたら検知してシステム管理者へ知らせる**（定期の検査 ＋ 計器 ＋ 警報）。
//
// ■ 知らせ方は既存の運用の経路である（ADR-0006・[[IADR-0165]]）。本器は計器とログを出し、警報のルール
//   `OwnerReadPolicyMissing` / `OwnerReadPolicyCheckSeriesAbsent`（`deploy/prometheus/alerts.yml` と写し 3 か所）が鳴らす。
// ■ 🔴 **opt-in にしない。** 内容の ABAC の門（DocumentService）より前に働いている必要がある（ADR-0121 決定 4）。
// ■ 🔴 **削除そのものは止めない**（SC-09 の口は変えない。ADR-0121 決定 2）。
public sealed record OwnerReadPolicyCheckOptions(TimeSpan Interval)
{
    public const string IntervalKey = "OwnerReadPolicyCheck:Interval";

    // 既定の周期。消えてから警報が鳴るまでの遅れは「周期 ＋ 警報の for（5 分）」である。
    // 1 回の検査はポリシーの表を 1 度読むだけで、表は管理者が手で作る数十件の桁である。
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    // 書式・範囲は `DepartmentAttributeSync:Interval` と同じ規則（`hh:mm:ss`・1 分以上 23:59:59 以下。理由はそちらの注記）。
    // 値域外は起動時に落とす —— 打ち間違いを既定へ黙って倒すと、設定したつもりの周期で回らない。
    public static OwnerReadPolicyCheckOptions FromConfiguration(IConfiguration configuration)
    {
        var declared = configuration[IntervalKey];
        if (string.IsNullOrWhiteSpace(declared))
            return new OwnerReadPolicyCheckOptions(DefaultInterval);
        if (TimeSpan.TryParseExact(declared.Trim(), @"hh\:mm\:ss",
                System.Globalization.CultureInfo.InvariantCulture, out var t)
            && t >= MinimumInterval)
            return new OwnerReadPolicyCheckOptions(t);
        throw new InvalidOperationException(
            $"{IntervalKey} の値 '{declared}' は不正である（hh:mm:ss 形式・00:01:00〜23:59:59。例 00:01:00）。");
    }
}

// 計器。🔴 **ゲージは「直近の検査で数えた件数」だけを出す。** 検査に失敗したとき・まだ 1 度も検査していないときは
// 系列を出さない —— 古い値を出すと消えたことを隠し、0 を出すと DB の障害を「ポリシーが無い」と偽る。
// 系列が無い状態は `OwnerReadPolicyCheckSeriesAbsent` が「見ていない」として知らせる。
public sealed class OwnerReadPolicyMetrics
{
    // 部門の同期と同じサービス名の Meter（Program.cs の AddMeter は 1 つで足りる）。
    public const string MeterName = "microservices-platform.authorization-service";

    // Prometheus 側の名前は `authz_owner_read_policy_active`（`.` は `_`。単位 `{policy}` は接尾辞にならない。
    // ゲージに `_total` は付かない）。scripts.repo.test.js が警報の式と突き合わせる。
    public const string ActiveGaugeName = "authz.owner_read_policy.active";
    public const string CheckCounterName = "authz.owner_read_policy.checks.total";
    public const string OutcomeTag = "authz.owner_read_policy.outcome";

    public const string OutcomePresent = "present";
    public const string OutcomeAbsent = "absent";
    public const string OutcomeFailed = "failed";

    private readonly Counter<long> _checks;
    private readonly object _gate = new();
    private int? _lastCount;

    public OwnerReadPolicyMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge(
            ActiveGaugeName, Observe, unit: "{policy}",
            description: "所有者の読み取りのポリシー（read・利用者の条件なし・文書の条件 owner ∈ {${current_user}} だけ）の有効な件数。"
                       + "0 なら所有者は自分の文書を読めない。直近の検査に失敗したとき・未検査のときは系列を出さない。");
        _checks = meter.CreateCounter<long>(
            CheckCounterName, unit: "{check}",
            description: "所有者の読み取りのポリシーの検査の結末。outcome = present / absent / failed。");
    }

    /// <summary>直近の検査で数えた件数（未検査・失敗は null）。</summary>
    public int? LastCount
    {
        get { lock (_gate) return _lastCount; }
    }

    public void RecordCount(int count)
    {
        lock (_gate) _lastCount = count;
        _checks.Add(1, new TagList { { OutcomeTag, count > 0 ? OutcomePresent : OutcomeAbsent } });
    }

    public void RecordFailure()
    {
        lock (_gate) _lastCount = null;
        _checks.Add(1, new TagList { { OutcomeTag, OutcomeFailed } });
    }

    private IEnumerable<Measurement<int>> Observe()
    {
        var count = LastCount;
        if (count is { } c) yield return new Measurement<int>(c);
    }
}

// 1 回の検査。DB を直接数える（門の問い合わせ〔gRPC〕も同じ関数を使う —— 判定を 2 つにしない）。
public sealed class OwnerReadPolicyCheck(
    AuthorizationDbContext db,
    OwnerReadPolicyMetrics metrics,
    ILogger<OwnerReadPolicyCheck> logger)
{
    // ログに添える形（投入の本文と同じ書き方）。
    internal const string ExpectedShape =
        "action=read・userConditions={}・documentConditions={\"owner\":[\"${current_user}\"]}";

    public static async Task<int> CountAsync(AuthorizationDbContext db, CancellationToken ct)
    {
        var candidates = await db.Policies.AsNoTracking()
            .Where(p => p.IsActive && p.Action == PolicyAction.Read)
            .ToListAsync(ct);
        return OwnerReadPolicyShape.CountActive(candidates);
    }

    /// <summary>数えて計器とログへ出す。数えられなければ null。取り消しはそのまま外へ出す。</summary>
    public async Task<int?> RunAsync(CancellationToken ct)
    {
        var previous = metrics.LastCount;
        int count;
        try
        {
            count = await CountAsync(db, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.RecordFailure();
            logger.LogError(ex,
                "所有者の読み取りのポリシーを数えられなかった（ポリシーの表を読めない）。在るかどうか分からないので計器の系列を止める"
                + "（警報 OwnerReadPolicyCheckSeriesAbsent）。次の周期で再試行する。");
            return null;
        }

        metrics.RecordCount(count);
        if (count == 0)
        {
            logger.LogError(
                "所有者の読み取りのポリシーが有効な状態で 1 件も無い。所有者は共有していない自分の文書を読めない"
                + "（削除・無効化・形の変更のいずれか）。運用仕様書の「所有者の読み取りのポリシーの投入」の手順で投入し直す。"
                + "形: {Shape}。", ExpectedShape);
        }
        else if (previous == 0)
        {
            logger.LogInformation("所有者の読み取りのポリシーが戻った（有効な件数 {Count}）。", count);
        }
        return count;
    }
}

// 常駐。起動時に 1 回、以後は周期ごとに検査する。1 回の失敗で常駐を止めない。
public sealed class OwnerReadPolicyCheckHostedService(
    IServiceScopeFactory scopeFactory,
    OwnerReadPolicyCheckOptions options,
    ILogger<OwnerReadPolicyCheckHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<OwnerReadPolicyCheck>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // RunAsync が数えられないことを自分で扱うので、ここへ来るのはスコープの解決などの想定外だけである。
                logger.LogError(ex, "所有者の読み取りのポリシーの検査で想定外の例外が発生した。次の周期で再試行する。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
