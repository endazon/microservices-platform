using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-12, UC-06, SC-07, IADR-0042 / IADR-0154: 変換ジョブ管理の BFF 集約。
// ConversionService（/jobs）へプロキシする。運用系のため管理者・運用者ロールに限定する
// （IADR-0042 §決定3。画面側は RequireRole で存在秘匿）。利用者の資格情報は後段へ伝播する。
//
// **認可はこの群の中で 2 段になっている**（05_screens:314「閲覧は管理者・運用者。再変換の実行と
// 人手補正は管理者限定」）:
//
// | 口 | 実効ロール | 根拠 |
// | --- | --- | --- |
// | `GET /`・`GET /{id}`（ジョブの照会） | admin ＋ operator | IADR-0128 決定 2（据え置き） |
// | `POST /{id}/retry`（再変換） | **admin のみ** | IADR-0128 決定 1 |
// | `GET /{id}/figures`・`.../image`・`POST .../correction`（**人手補正**） | **admin のみ** | IADR-0154 決定 6 |
//
// **「人手補正」と「再変換」を同じ語で呼ばない。** 前者は成功ジョブの縮退した図をコード化し直す
// 操作（Phase 1）で、後者は失敗ジョブを原本からやり直す操作である（IADR-0154 決定 5）。
public static class ConversionBffEndpoints
{
    public static IEndpointRouteBuilder MapConversionBffEndpoints(this IEndpointRouteBuilder app)
    {
        // 照会・再変換に共通の下限。再変換はさらに下（MapPost）で管理者ロールへ絞る。
        var g = app.MapGroup("/bff/conversion/jobs")
            .WithTags("Conversion BFF")
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        // 一覧（?status=failed 等で絞り込み）。
        // SC-07 は変換状況の可視化が目的の運用画面のため、後段障害を空一覧へ縮退させない（「ジョブ無し」と
        // 「サービス障害」を誤認させない）。非 2xx はそのまま伝播し、後段不達は 502 で可視化する。
        // 個別取得・再変換と挙動を揃える（レビュー #172 指摘対応）。
        g.MapGet("/", async (IHttpClientFactory httpFactory, HttpContext http, string? status, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var path = string.IsNullOrWhiteSpace(status) ? "/jobs" : $"/jobs?status={Uri.EscapeDataString(status)}";
            try
            {
                var resp = await client.GetAsync(path, ct);
                if (!resp.IsSuccessStatusCode)
                    return Results.StatusCode((int)resp.StatusCode);
                var jobs = await resp.Content.ReadFromJsonAsync<List<ConversionJobDto>>(ct);
                return Results.Ok(jobs ?? []);
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        }).WithName("BffConversionJobList").Produces<List<ConversionJobDto>>();

        // 個別取得。
        g.MapGet("/{id:guid}", async (Guid id, IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var resp = await client.GetAsync($"/jobs/{id}", ct);
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode((int)resp.StatusCode);
            var job = await resp.Content.ReadFromJsonAsync<ConversionJobDto>(ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        }).WithName("BffConversionJobGet").Produces<ConversionJobDto>();

        // **再変換**（人手補正ではない）。後段は 202 / 404 / 409（失敗以外は再変換不可）を返す。
        // FR-12, SC-07（2026-08-04 確定）: 再変換の実行権限は管理者ロールに限る。画面のアクセス制御と
        // API の権限を揃える（API 側だけ緩いと画面の制御が意味を持たないため）。
        // 認可メタデータは AND 合成されるため、実効要件はグループの「admin または operator」と
        // AdminOnly の積＝**admin のみ**になる。グループを絞らないのは、閲覧ロールの裁定が計画側で
        // 継続中で、照会（GET）を巻き添えで変えないためである（IADR-0128 決定1・決定2）。
        //
        // IADR-0154 決定 4: 補正のあるジョブへの再変換は後段が 409 corrections_would_be_lost を
        // 返す。**本文ごと透過する**——従前は Results.StatusCode で状態コードだけを返しており、
        // 409 の本文（correctedFigures）が画面へ届かなかった。#640 で usageCount が届かなかったのと
        // 同型であり、そのとき直したのは解析層（ApiError）だけで、中継層はここに残っていた。
        g.MapPost("/{id:guid}/retry", async (Guid id, bool? discardCorrections,
            IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var path = discardCorrections == true
                ? $"/jobs/{id}/retry?discardCorrections=true"
                : $"/jobs/{id}/retry";
            var resp = await client.PostAsync(path, content: null, ct);
            return await RelayAsync(resp, ct);
        }).WithName("BffConversionJobRetry")
          .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // UC-06, SC-07, IADR-0154: 人手補正 Phase 1 の本文取得（2 ペインの材料）。
        // **人手補正は管理者限定**（05_screens:314「再変換の実行と人手補正は管理者限定」）。
        // 2 ペインを開く操作そのものが人手補正であるため、照会（GET）だが AdminOnly を積む。
        g.MapGet("/{id:guid}/figures", async (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var resp = await client.GetAsync($"/jobs/{id}/figures", ct);
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode((int)resp.StatusCode);
            var figures = await resp.Content.ReadFromJsonAsync<List<ConversionFigureDto>>(ct);
            return Results.Ok(figures ?? []);
        }).WithName("BffConversionJobFigureList")
          .Produces<List<ConversionFigureDto>>()
          .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // UC-06, SC-07, IADR-0154 決定 2: 元の図画像。**BFF がオブジェクトストレージから解決して返す**
        // （DocumentBffEndpoints の GET /bff/documents/{id}/content と同じ形）。ワーカーへ画像配信を
        // 足さないのは IADR-0029（ワーカーは最小 HTTP サーフェス）に従うためである。
        g.MapGet("/{id:guid}/figures/{figureId}/image", async (Guid id, string figureId,
            IHttpClientFactory httpFactory, IObjectStorageClient storage, HttpContext http,
            CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var resp = await client.GetAsync($"/jobs/{id}/figures", ct);
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode((int)resp.StatusCode);
            var figures = await resp.Content.ReadFromJsonAsync<List<ConversionFigureDto>>(ct);
            var figure = figures?.FirstOrDefault(f => f.FigureId == figureId);
            // 未知の図と、画像を持たない図（コード化済み）を区別しない——どちらも「その画像は無い」。
            if (figure?.ImageUri is null || !storage.CanResolve(figure.ImageUri))
                return Results.NotFound();
            var bytes = await storage.GetBytesAsync(figure.ImageUri, ct);
            return Results.File(bytes, figure.ImageContentType ?? "application/octet-stream");
        }).WithName("BffConversionJobFigureImage")
          .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // UC-06, SC-07, IADR-0154: 人手補正の投稿。後段の 409（figure_not_correctable / job_busy /
        // body_unavailable）は**本文ごと透過**する——画面が理由で出し分けるためである。
        g.MapPost("/{id:guid}/figures/{figureId}/correction", async (Guid id, string figureId,
            FigureCorrectionRequest request, IHttpClientFactory httpFactory, HttpContext http,
            CancellationToken ct) =>
        {
            var client = Forwarding(httpFactory, http);
            var resp = await client.PostAsJsonAsync(
                $"/jobs/{id}/figures/{Uri.EscapeDataString(figureId)}/correction", request, ct);
            return await RelayAsync(resp, ct);
        }).WithName("BffConversionJobFigureCorrection")
          .Produces<FigureCorrectionResultDto>()
          .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        return app;
    }

    // IADR-0040 の透過中継: 後段の本文・状態コード・Content-Type をそのまま返す。
    // 詰め替えると 409 の理由や件数が画面まで届かない（#640 の実測）。
    private static async Task<IResult> RelayAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var content = await resp.Content.ReadAsStringAsync(ct);
        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        return string.IsNullOrEmpty(content)
            ? Results.StatusCode((int)resp.StatusCode)
            : Results.Content(content, contentType, statusCode: (int)resp.StatusCode);
    }

    private static HttpClient Forwarding(IHttpClientFactory httpFactory, HttpContext http)
    {
        var client = httpFactory.CreateClient("ConversionService");
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
        return client;
    }

    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested;
}
