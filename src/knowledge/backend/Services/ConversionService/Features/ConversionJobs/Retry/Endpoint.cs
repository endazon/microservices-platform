using ConversionService.Domain;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Wolverine;

namespace ConversionService.Features.ConversionJobs.Retry;

// FR-12 例外フロー / UC-06: **再変換**。失敗ジョブのみ再変換する（原本イベントを再発行）。
// **人手補正（図のコード化のやり直し）とは別の操作である**——後者は `CorrectFigure` が担う。
// 未知の id は 404。失敗以外（processing/succeeded/queued）は 409（再変換不可）。UI 制御だけに頼らず
// API 側でも状態を強制し、処理中の二重発行・成功済みの不要な再処理を防ぐ（レビュー #172 指摘対応）。
//
// IADR-0154 決定 4: 補正のあるジョブは既定で拒否し、409 corrections_would_be_lost を返す。
// 破棄してよい場合だけ ?discardCorrections=true を付けて再送する（補正版を正とする。
// 05_screens:333）。**確認をダイアログだけに置くと、生成クライアントや別経路の呼び出しが
// 素通りする**ため、API 側で強制する。
internal static class RetryConversionJobEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/{id:guid}/retry", async (Guid id, IConversionJobStore store,
            IMessageBus bus, bool? discardCorrections, CancellationToken ct) =>
        {
            var job = await store.GetAsync(id, ct);
            if (job is null)
                return Results.NotFound();
            if (job.Status != ConversionJobStatus.Failed)
                return Results.Conflict(new { error = "not_retryable", status = job.Status });

            // **［到達経路の注記 / PR #650 レビュー 1 巡目］この分岐は「人が手で retry を押す」主経路では
            // 発火しない。** 直上で `failed` 以外を弾いているのに対し、補正は `succeeded` のジョブへ入る
            // ためである（実測: 補正済みの succeeded ジョブへ retry すると `not_retryable` で止まる。
            // `Retry_CorrectionsGate_IsReachable_ThroughPublicApiOnly` が固定している）。
            // **発火するのは「一度成功して図が記録されたジョブが、その後の変換で失敗した」場合**である
            // ——図は `MarkFailed` で消えないので補正が残り、そこへ retry が来る。稀だが実在する経路で
            // あり、**そのときこそ補正が黙って消えてはならない**ので分岐は残す。
            // これを書いておかないと、次に読む人が「到達しない分岐」と判断して外しかねない。
            var discard = discardCorrections == true;
            if (!discard && job.HasCorrection)
            {
                var figures = await store.ListFiguresAsync(id, ct);
                return Results.Conflict(new
                {
                    error = "corrections_would_be_lost",
                    status = job.Status,
                    correctedFigures = figures?.Count(f => f.Corrected) ?? 0,
                });
            }

            var ev = await store.PrepareRetryAsync(id, discard, ct);
            if (ev is null)
                return Results.Conflict(new { error = "not_retryable", status = job.Status }); // 競合で状態が変わった等
            // 🔴 ADR-0027 / #441 E1: **辺 RawDocumentFetched の 2 つ目の発行元である。**
            // 型名が発行行に現れないため `check-event-topology.js` からは見えない
            // （IADR-0245 決定 6-2）。ここを MassTransit のまま残すと、購読側は Wolverine なので
            // **再変換は受理されたまま永久に実行されず、キュー深さのアラームも鳴らない**
            // （Wolverine が受け取って捨てるため。IADR-0245 の実測）。**辺は原子的に動かす。**
            await bus.PublishAsync(ev);
            return Results.Accepted($"/jobs/{id}");
        }).WithName("ConversionJobRetry");
    }
}
