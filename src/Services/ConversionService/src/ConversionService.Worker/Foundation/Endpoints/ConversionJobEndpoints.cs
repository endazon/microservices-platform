using ConversionService.Worker.Foundation.Jobs;
using KnowledgePlatform.Shared.Contracts.Dtos;
using MassTransit;

namespace ConversionService.Worker.Foundation.Endpoints;

// FR-12, UC-06, SC-07, IADR-0042: 変換ジョブの状況照会・人手補正（再変換）エンドポイント。
// メッシュ内部の管理 API（BFF からのみ到達。ingress へは公開しない）。認可は BFF 側で管理者・運用者に
// 限定する（IADR-0039 と同方針）。ワーカー自身は最小 HTTP サーフェスに留め、ここでは認可を課さない。
public static class ConversionJobEndpoints
{
    public static IEndpointRouteBuilder MapConversionJobEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/jobs").WithTags("Conversion Jobs");

        // 一覧（?status=failed 等で絞り込み。新しい順）。
        g.MapGet("/", (IConversionJobStore store, string? status) =>
            Results.Ok(store.List(status)))
            .WithName("ConversionJobList").Produces<List<ConversionJobDto>>();

        // 個別取得。
        g.MapGet("/{id:guid}", (Guid id, IConversionJobStore store) =>
        {
            var job = store.Get(id);
            return job is null ? Results.NotFound() : Results.Ok(job);
        }).WithName("ConversionJobGet").Produces<ConversionJobDto>();

        // FR-12 例外フロー / UC-06: 人手補正。失敗ジョブを再変換する（原本イベントを再発行）。
        // 未知の id は 404。既知なら queued に戻して原本を再発行し 202 を返す。
        g.MapPost("/{id:guid}/retry", async (Guid id, IConversionJobStore store,
            IPublishEndpoint bus, CancellationToken ct) =>
        {
            var ev = store.PrepareRetry(id);
            if (ev is null)
                return Results.NotFound();
            await bus.Publish(ev, ct);
            return Results.Accepted($"/jobs/{id}");
        }).WithName("ConversionJobRetry");

        return app;
    }
}
