using GraphService.Domain.Ports;
using Microsoft.Extensions.Options;

namespace GraphService.Features.Clustering.Summarize;

// FR-17, FR-18, SC-10, ADR-0035 決定 3・6, ADR-0083 決定 2・3, [[IADR-0430]] 決定 5・6 (#1395):
// クラスタ要約の生成を回す**日次バッチ**。
//
// `ADR-0035` 決定 3 が「実行タイミングは**定期バッチ（日次）**」と定めており、決定 6 が
// 「変更のあったクラスタだけを再生成する」と定めている。したがって周期は 24 時間であり、
// **文書の更新ごとに走らせない**（「取り込みごとの再計算は要約の再生成が連鎖して費用が読めない」）。
//
// 形は `ClusterDetectionHostedService` に合わせる（`BackgroundService` ＋ `PeriodicTimer` ＋
// 初回は 1 周期後 ＋ 排他リースのゲート ＋ `TryRunCycleAsync` を internal にして決定的に検証）。
//
// 🔴 **既定では 1 周期も回さない**（`ClusterSummary:Enabled=false`）。無効なら**タイマーすら作らず**
// 即座に降りる —— リースも取らず、DB も読まず、LLM の呼び出しは 1 回も起きない（[[IADR-0430]] 決定 6）。
// **ゲートはホストの中に置く**（DI の登録を構成で分岐させない）。分岐を登録側へ出すと、
// 構成の効き方が「組み立て時に読めたかどうか」に依存し、**試験から確かめられない**。
//
// **検出との順序は取らない。** 検出（`ClusterDetectionHostedService`）が先に走ろうと後に走ろうと、
// 本ジョブは「そのとき永続化されているクラスタ」を見て、要約が要るものを作り直す ——
// 判定は時刻の比較であり（`ADR-0083` 決定 3）、**取りこぼしても次周期で拾う。**
// 順序を仮定した設計にすると、片方が失敗した周期で不変条件が崩れる。
public sealed class ClusterSummaryHostedService(
    IServiceScopeFactory scopeFactory,
    IClusterSummaryLeaseCoordinator leaseCoordinator,
    IOptions<ClusterSummaryOptions> options,
    ILogger<ClusterSummaryHostedService> logger) : BackgroundService
{
    // ADR-0035 決定 3: **日次**。
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation(
                "クラスタ要約の生成は無効である（既定）。有効化は構成 {Key}:Enabled=true である。",
                ClusterSummaryOptions.SectionName);
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TryRunCycleAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 1 周期の失敗でホストを落とさない（本サービスは DocumentUpdated /
                    // DocumentDeleted の購読者でもある。要約の都合で購読を止めない）。
                    logger.LogError(ex, "クラスタ要約の生成に失敗した。次周期で再試行する。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // シャットダウン。
        }
    }

    // 🔴 単一書き手化のゲート。**リースを取得できたレプリカだけが生成する。**
    // 取得できない周期は**読み込みもしない**（スキップ＝fail-safe）——
    // 二重に走ると壊れるのは行ではなく**費用**である（LLM 呼び出しが倍になる）。
    // 戻り値は実行したか。
    internal async Task<bool> TryRunCycleAsync(CancellationToken ct)
    {
        // 🔴 **無効なら何もしない。** ここにも置くのは、周期の中で構成が読み直されることを
        // 試験から確かめられるようにするためである（`ExecuteAsync` の早期リターンだけだと、
        // 「回っていないこと」を決定的に測れない）。
        if (!options.Value.Enabled)
            return false;

        await using var lease = await leaseCoordinator.TryAcquireAsync(ct);
        if (lease is null)
        {
            logger.LogDebug(
                "クラスタ要約生成のリースを取得できなかった（他レプリカが実行中）。本周期をスキップする。");
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<ClusterSummaryJob>();
        await job.RunAsync(ct);
        return true;
    }
}
