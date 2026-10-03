using GraphService.Common.Observability;
using GraphService.Domain.Ports;
using Microsoft.Extensions.Options;

namespace GraphService.Features.Clustering.Detect;

// FR-17, FR-18, SC-10, SC-18, ADR-0035 決定 3, ADR-0083 決定 1, [[IADR-0425]] (#1363):
// クラスタ検出の**日次バッチ**。
//
// 計画 ADR-0035 決定 3 が実行タイミングを確定している ——
// 「実行タイミングは**定期バッチ（日次）**とする。……**取り込みごとの再計算は要約の再生成が
//   連鎖して費用が読めない**」。したがって周期は 24 時間であり、**文書の更新ごとに走らせない。**
//
// ［2026-10-04 / #1733・[[IADR-0496]]］🔴 **周期の位相は前回の成功に付ける。プロセスの起動に付けない。**
// 従前は `PeriodicTimer(1 日)` で、初回が**起動の 24 時間後**だった。稼働 PoC は Pod がほぼ毎日作り直されるので
// 拍が永久に来ず、9/27 から 1 度も検出が走らなかった。いまの形は次のとおり。
//
//  1. 起動の後、短い待ち（`ClusterDetection:StartupDelay`。既定 2 分）を置いて 1 度判定する。
//  2. 判定は**リースの内側で** `graph_batch_runs` の行を読み直して行う（ローリング更新の新旧 2 Pod が両方走らない）。
//     前回の成功から 24 時間以上（または記録が無い）なら走らせ、そうでなければ「前回 + 24 時間」まで眠る。
//  3. リースが取れなければ `ClusterDetection:RetryDelay`（既定 1 時間）後に判定し直す。周期が失敗した（取り消し含む）ら、
//     **連続した失敗の数だけ再試行の待ちを倍々にする**（1 時間 → 2 → 4 → … → 24 時間で頭打ち）。
//     失敗は成功として記録しない（記録は検出の保存と同じ `SaveChanges`。`ClusterDetectionJob`）。
//     試行は本体の前に別の保存で記録するので、**プロセスごと落ちた試行も**次の起動がバックオフに数える
//     （メモリ不足で落ちる検出が「再起動 → 2 分後に再実行 → 落ちる」を繰り返さない）。
//
// `TryRunCycleAsync` は internal にして決定的に検証する（形は従前と同じ）。
public sealed class ClusterDetectionHostedService(
    IServiceScopeFactory scopeFactory,
    IClusterDetectionLeaseCoordinator leaseCoordinator,
    IOptions<ClusterDetectionOptions> options,
    ClusterDetectionMetrics metrics,
    ILogger<ClusterDetectionHostedService> logger) : BackgroundService
{
    // ADR-0035 決定 3: **日次**。
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    // #1598: 周期の実際の長さ。**試験だけが短くする**（周期は待てない）。本番の組み立ては触らない。
    internal TimeSpan CycleInterval { get; init; } = Interval;

    // #1622 / #1733: 待ちと期限の判定の時計。**試験だけが偽の時計（FakeTimeProvider）に差し替える**。本番はシステムの時計のまま。
    internal TimeProvider CycleClock { get; init; } = TimeProvider.System;

    // [[IADR-0496]] 決定 4: このプロセスで続いている失敗の数（例外で終わった判定）。成功・期限内・リース無しで 0 に戻る。
    // DB へ届かない失敗（接続不能）でもバックオフさせるための数であり、プロセスを跨ぐ数は `graph_batch_runs` が持つ。
    private int _consecutiveFailures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (opts.HasInvalidValue)
        {
            logger.LogWarning(
                "{Section} の待ちが不正である（StartupDelay={StartupDelay} RetryDelay={RetryDelay}）。既定へ倒す。",
                ClusterDetectionOptions.SectionName, opts.StartupDelay, opts.RetryDelay);
        }

        try
        {
            await Task.Delay(opts.EffectiveStartupDelay, CycleClock, stoppingToken);
            while (true)
            {
                TimeSpan wait;
                try
                {
                    wait = (await TryRunCycleAsync(stoppingToken)).NextDelay;
                    _consecutiveFailures = 0;
                }
                // ［2026-09-26 / #1598・[[IADR-0299]] 追記］🔴 **素通しするのは停止要求（stoppingToken）の取り消しだけである。**
                // 下流の時間切れ等の取り消しは周期の失敗であり、型だけで素通しすると外側で「シャットダウン」と読まれて
                // ループが**永久に**終わる。形は DriftDetectionHostedService（#1382）と同じ。
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // 1 周期の失敗でホストを落とさない（本サービスは DocumentUpdated /
                    // DocumentDeleted の購読者でもある。クラスタ検出の都合で購読を止めない）。
                    // 🔴 [[IADR-0496]]: 失敗は成功として記録されていないので、次の判定は「まだ期限切れ」を見て走り直す。
                    _consecutiveFailures++;
                    wait = ClusterDetectionSchedule.Backoff(opts.EffectiveRetryDelay, _consecutiveFailures, CycleInterval);
                    logger.LogError(ex, "クラスタ検出に失敗した（連続 {Failures} 回）。{Wait} 後に判定し直す。",
                        _consecutiveFailures, wait);
                }

                await Task.Delay(wait, CycleClock, stoppingToken);
            }
        }
        // #1598: 想定外の取り消しを黙って「シャットダウン」と読まない（届いたら例外のまま出す）。
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // シャットダウン。走っている途中の検出は保存されず、成功も記録されない（次の起動で追いつく）。
        }
    }

    // 🔴 単一書き手化のゲート。**リースを取得できたレプリカだけが判定し、検出する。**
    // 取得できない周期は**読み込みもしない**（スキップ＝fail-safe）。
    //
    // 🔴 [[IADR-0496]] 決定 3: **期限の判定はリースの内側で、記録を読み直してから行う。** リースの外で読んだ値で
    // 決めると、他の Pod が検出を終えてリースを放した直後に取ったこちらが、古い記録のまま 2 回目を走らせる。
    internal async Task<ClusterDetectionCycleOutcome> TryRunCycleAsync(CancellationToken ct)
    {
        var retryDelay = options.Value.EffectiveRetryDelay;

        await using var lease = await leaseCoordinator.TryAcquireAsync(ct);
        if (lease is null)
        {
            logger.LogDebug(
                "クラスタ検出のリースを取得できなかった（他レプリカが実行中）。{RetryDelay} 後に判定し直す。", retryDelay);
            return new(ClusterDetectionCycleKind.LeaseUnavailable, retryDelay);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<ClusterDetectionJob>();

        var state = await job.ReadRunStateAsync(ct);
        metrics.RecordLastSuccess(state.LastSucceededAt);

        var now = CycleClock.GetUtcNow();
        var wait = ClusterDetectionSchedule.NextDelay(now, state, CycleInterval, retryDelay);
        if (wait > TimeSpan.Zero)
        {
            logger.LogInformation(
                "クラスタ検出はまだ待つ（前回の成功 {LastSucceededAt}・成功していない試行 {Attempts} 回・最後の試行 {LastAttemptedAt}）。{Wait} 後に判定する。",
                state.LastSucceededAt?.ToString("O") ?? "記録なし", state.AttemptsSinceSuccess,
                state.LastAttemptedAt?.ToString("O") ?? "記録なし", wait);
            return new(ClusterDetectionCycleKind.NotDue, wait);
        }

        logger.LogInformation(
            "クラスタ検出の期限が来ている（前回の成功 {LastSucceededAt}・成功していない試行 {Attempts} 回）。検出する。",
            state.LastSucceededAt?.ToString("O") ?? "記録なし", state.AttemptsSinceSuccess);

        // 🔴 試行は本体より先に、別の保存で確定させる（落ちても残す。決定 4）。
        await job.RecordAttemptAsync(now, ct);

        var startedAt = CycleClock.GetTimestamp();
        var result = await job.RunAsync(ct);
        // 2 回目の更新は DB を読み直さない。いま本体が書いた成功の値（同じ保存で確定済み）をそのまま流用する。
        metrics.RecordLastSuccess(result.StartedAt);

        // 次は「この成功 + 1 周期」。検出が 1 周期より長くかかった（あるいは時計が食い違った）ときに
        // 間を空けずに回り続けないよう、再試行の待ちを下限にする。
        var next = ClusterDetectionSchedule.DelayUntilDue(CycleClock.GetUtcNow(), result.StartedAt, CycleInterval);
        if (next < retryDelay)
            next = retryDelay;

        logger.LogInformation(
            "クラスタ検出を終えた（所要 {Elapsed}）。{Next} 後に判定する。",
            CycleClock.GetElapsedTime(startedAt), next);
        return new(ClusterDetectionCycleKind.Ran, next);
    }
}

// [[IADR-0496]]: 1 回の判定の結末と、次の判定までの待ち。
internal enum ClusterDetectionCycleKind
{
    // リースを取れなかった（他レプリカが実行中・取得の一時障害）。再試行の待ちの後に判定し直す。
    LeaseUnavailable,

    // 前回の成功から 1 周期たっていない。期限まで眠る。
    NotDue,

    // 検出した（成功を記録した）。
    Ran,
}

internal readonly record struct ClusterDetectionCycleOutcome(ClusterDetectionCycleKind Kind, TimeSpan NextDelay)
{
    public bool Ran => Kind == ClusterDetectionCycleKind.Ran;
}
