using ConversionService.Worker.Foundation.Jobs;
using Platform.Shared.Contracts.Dtos;
using MassTransit;

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

        // FR-12 例外フロー / UC-06: 人手補正。失敗ジョブのみ再変換する（原本イベントを再発行）。
        // 未知の id は 404。失敗以外（processing/succeeded/queued）は 409（再変換不可）。UI 制御だけに頼らず
        // API 側でも状態を強制し、処理中の二重発行・成功済みの不要な再処理を防ぐ（レビュー #172 指摘対応）。
        g.MapPost("/{id:guid}/retry", async (Guid id, IConversionJobStore store,
            IPublishEndpoint bus, CancellationToken ct) =>
        {
            var job = await store.GetAsync(id, ct);
            if (job is null)
                return Results.NotFound();
            if (job.Status != ConversionJobStatus.Failed)
                return Results.Conflict(new { error = "not_retryable", status = job.Status });

            var ev = await store.PrepareRetryAsync(id, ct);
            if (ev is null)
                return Results.Conflict(new { error = "not_retryable", status = job.Status }); // 競合で状態が変わった等
            await bus.Publish(ev, ct);
            return Results.Accepted($"/jobs/{id}");
        }).WithName("ConversionJobRetry");

        return app;
    }
}
