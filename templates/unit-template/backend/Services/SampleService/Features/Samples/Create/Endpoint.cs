namespace SampleService.Features.Samples.Create;

// テンプレート: スライスの HTTP 入口。Endpoint は薄く保ち、判断は Handler / Domain へ置く（IADR-0282）。
// 契約は docs/api/openapi.yaml で管理する。
public static class CreateSampleEndpoint
{
    public static IEndpointRouteBuilder MapCreateSample(this IEndpointRouteBuilder app)
    {
        app.MapPost("/samples", (CreateSample command, TimeProvider clock)
            => Results.Ok(CreateSampleHandler.Handle(command, clock)));
        return app;
    }
}
