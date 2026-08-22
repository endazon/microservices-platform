using ConversionService.Worker.Foundation.Jobs;
using ConversionService.Worker.Foundation.Services;
using Knowledge.Contracts.Dtos;
using Wolverine;

namespace ConversionService.Worker.Foundation.Endpoints;

// FR-12, UC-06, SC-07, IADR-0042: 変換ジョブの状況照会・人手補正（再変換）エンドポイント。
// メッシュ内部の管理 API（BFF からのみ到達。ingress へは公開しない）。認可は BFF 側で管理者・運用者に
// 限定する（IADR-0042 §決定3）。ワーカー自身は最小 HTTP サーフェスに留め、ここでは認可を課さない。
public static class ConversionJobEndpoints
{
    public static IEndpointRouteBuilder MapConversionJobEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/jobs").WithTags("Conversion Jobs");

        // 一覧（?status=failed 等で絞り込み。新しい順）。
        g.MapGet("/", async (IConversionJobStore store, string? status, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(status, ct)))
            .WithName("ConversionJobList").Produces<List<ConversionJobDto>>();

        // 個別取得。
        g.MapGet("/{id:guid}", async (Guid id, IConversionJobStore store, CancellationToken ct) =>
        {
            var job = await store.GetAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        }).WithName("ConversionJobGet").Produces<ConversionJobDto>();

        // FR-12 例外フロー / UC-06: **再変換**。失敗ジョブのみ再変換する（原本イベントを再発行）。
        // **人手補正（図のコード化のやり直し）とは別の操作である**——後者は下の /figures 系が担う。
        // 未知の id は 404。失敗以外（processing/succeeded/queued）は 409（再変換不可）。UI 制御だけに頼らず
        // API 側でも状態を強制し、処理中の二重発行・成功済みの不要な再処理を防ぐ（レビュー #172 指摘対応）。
        //
        // IADR-0154 決定 4: 補正のあるジョブは既定で拒否し、409 corrections_would_be_lost を返す。
        // 破棄してよい場合だけ ?discardCorrections=true を付けて再送する（補正版を正とする。
        // 05_screens:333）。**確認をダイアログだけに置くと、生成クライアントや別経路の呼び出しが
        // 素通りする**ため、API 側で強制する。
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

        // UC-06, SC-07, IADR-0154: 人手補正 Phase 1 の本文取得。2 ペイン（左＝図コード・右＝元の図画像）
        // が要る材料を返す。**画像のバイト列は返さない**——一覧が図の枚数だけ膨らむため、BFF が
        // 別の口で配信する（IADR-0154 決定 2）。
        g.MapGet("/{id:guid}/figures", async (Guid id, IConversionJobStore store, CancellationToken ct) =>
        {
            var figures = await store.ListFiguresAsync(id, ct);
            return figures is null ? Results.NotFound() : Results.Ok(figures);
        }).WithName("ConversionJobFigureList").Produces<List<ConversionFigureDto>>();

        // UC-06, SC-07, IADR-0154: 人手補正の投稿。**Phase 1 は縮退した図に限る**（05_screens:330）。
        g.MapPost("/{id:guid}/figures/{figureId}/correction", async (Guid id, string figureId,
            FigureCorrectionRequest request, IFigureCorrectionService corrections, CancellationToken ct) =>
        {
            // IADR-0154 決定 3: 空だけでなく、**コードフェンスを内側から閉じられる入力**も弾く。
            // 保存された本文は再発行されて一般利用者が読むため、ここを通すと文書が壊れる
            // （PR #650 レビュー 2 巡目）。
            if (!FigureMarkdown.IsEmbeddable(request.Language, request.Code))
                return Results.BadRequest(new { error = "invalid_correction" });

            var outcome = await corrections.CorrectAsync(id, figureId, request, ct);
            return outcome.Status switch
            {
                FigureCorrectionStatus.Applied => Results.Ok(new FigureCorrectionResultDto(
                    figureId, outcome.MarkdownUri!, outcome.CorrectedFigures)),
                FigureCorrectionStatus.JobNotFound or FigureCorrectionStatus.FigureNotFound
                    => Results.NotFound(),
                FigureCorrectionStatus.NotCorrectable
                    => Results.Conflict(new { error = "figure_not_correctable" }),
                FigureCorrectionStatus.JobBusy
                    => Results.Conflict(new { error = "job_busy" }),
                // 本文が読めない・埋め込みが見つからない。**補正は保存していない。**
                _ => Results.Conflict(new { error = "body_unavailable" }),
            };
        }).WithName("ConversionJobFigureCorrection");

        return app;
    }
}
