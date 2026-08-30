using ConversionService.Features.ConversionJobs.CorrectFigure;
using ConversionService.Features.ConversionJobs.GetById;
using ConversionService.Features.ConversionJobs.List;
using ConversionService.Features.ConversionJobs.ListFigures;
using ConversionService.Features.ConversionJobs.Retry;

namespace ConversionService.Features.ConversionJobs;

// FR-12, UC-06, SC-07, IADR-0042: 変換ジョブ集約の登録表（ADR-0068 決定 1）。
// メッシュ内部の管理 API（BFF からのみ到達。ingress へは公開しない）。認可は BFF 側で管理者・運用者に
// 限定する（IADR-0042 §決定3）。ワーカー自身は最小 HTTP サーフェスに留め、ここでは認可を課さない。
//
// `MapGroup` とタグ付けは集約の全操作が使うものであり、特定の 1 操作に属さない。
// 各操作の処理は `Features/ConversionJobs/<操作>/` に居る（ADR-0065 決定 2）。
public static class ConversionJobEndpoints
{
    public static IEndpointRouteBuilder MapConversionJobEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/jobs").WithTags("Conversion Jobs");

        ListConversionJobsEndpoint.Map(g);
        GetConversionJobEndpoint.Map(g);
        RetryConversionJobEndpoint.Map(g);
        ListConversionFiguresEndpoint.Map(g);
        CorrectFigureEndpoint.Map(g);

        return app;
    }
}
