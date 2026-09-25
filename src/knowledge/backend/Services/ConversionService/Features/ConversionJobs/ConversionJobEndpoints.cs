using ConversionService.Features.ConversionJobs.CorrectFigure;
using ConversionService.Features.ConversionJobs.GetById;
using ConversionService.Features.ConversionJobs.List;
using ConversionService.Features.ConversionJobs.ListFigures;
using ConversionService.Features.ConversionJobs.Retry;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace ConversionService.Features.ConversionJobs;

// FR-12, UC-06, SC-07, IADR-0042: 変換ジョブ集約の登録表（ADR-0068 決定 1）。
// メッシュ内部の管理 API（BFF からのみ到達。ingress へは公開しない）。
//
// NFR-09, ADR-0109 決定 3, ADR-0084 決定 1, IADR-0465 (#1520): **BFF が中継した利用者の資格情報で門を判定する**。
// 従前は「認可は BFF 側で課し、ワーカーには課さない」（IADR-0042 決定 3・IADR-0403 決定 4）だったが、BFF の中継は
// エッジであり east-west の gRPC 化で閉じる道が無くなったため（ADR-0109 決定 1）、後段が自ら検証する。
// 実効ロールは **BFF の門（`ConversionBffEndpoints`）と同じ**にする —— 片側だけ緩いと、BFF を迂回した
// メッシュ内の直呼びで緩い側が効く（DataSourceService と同じ二重化）:
//
// | 口 | 実効ロール | 門の所在 |
// | --- | --- | --- |
// | `GET /jobs`・`GET /jobs/{id}`（照会） | admin ＋ operator | 群（本ファイル） |
// | `POST /jobs/{id}/retry`（再変換） | **admin のみ** | 群 ∧ `AdminOnly`（`Retry/Endpoint.cs`） |
// | `GET /jobs/{id}/figures`（人手補正の材料） | **admin のみ** | 群 ∧ `AdminOnly`（`ListFigures/Endpoint.cs`） |
// | `POST /jobs/{id}/figures/{figureId}/correction`（人手補正） | **admin のみ** | 群 ∧ `AdminOnly`（`CorrectFigure/Endpoint.cs`） |
//
// 🔴 **`ServiceCaller` は足さない —— `platform-service` だけを持つサービス間トークンは通らない。** `/jobs` を
// サービスとして呼ぶ呼び出し元は無い（呼び出し元は BFF の中継だけ）。サービス間の呼び出しが要るなら、それは
// east-west であり gRPC と `ServiceCaller` の面で作る（ADR-0029・IADR-0379 決定 4）。
// ただし門はロールで判定し主体の種別は見ないので、**門のロールを持つ realm のサービスアカウントは通る**
// （BFF・DataSourceService と同じ性質。IADR-0465 決定 2）。
//
// `MapGroup` とタグ付けは集約の全操作が使うものであり、特定の 1 操作に属さない。
// 各操作の処理は `Features/ConversionJobs/<操作>/` に居る（ADR-0065 決定 2）。
public static class ConversionJobEndpoints
{
    public static IEndpointRouteBuilder MapConversionJobEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/jobs").WithTags("Conversion Jobs")
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        ListConversionJobsEndpoint.Map(g);
        GetConversionJobEndpoint.Map(g);
        RetryConversionJobEndpoint.Map(g);
        ListConversionFiguresEndpoint.Map(g);
        CorrectFigureEndpoint.Map(g);

        return app;
    }
}
