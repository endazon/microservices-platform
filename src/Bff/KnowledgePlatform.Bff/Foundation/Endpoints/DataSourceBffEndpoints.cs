using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using System.Net.Http.Json;

namespace KnowledgePlatform.Bff.Foundation.Endpoints;

// FR-01, FR-02, UC-04, SC-06, IADR-0039: データソース管理の BFF 集約。
// DataSourceService（/datasources）へプロキシする。データソースは文書 ABAC のスコープ対象ではなく
// システム運用資産のため、閲覧・操作は管理者・運用者ロールに限定する（画面側は RequireRole で存在秘匿）。
// 利用者の資格情報（Authorization）は後段へ伝播する（将来の後段認可・監査の一貫性のため）。
public static class DataSourceBffEndpoints
{
    public static IEndpointRouteBuilder MapDataSourceBffEndpoints(this IEndpointRouteBuilder app)
    {
        // SC-06/IADR-0039: 管理系は platform-admin もしくは platform-operator に限定する。
        var g = app.MapGroup("/bff/datasources")
            .WithTags("DataSources BFF")
            .RequireAuthorization(p => p.RequireRole(
                KnowledgePlatformAuthPolicies.AdminRole,
                KnowledgePlatformAuthPolicies.OperatorRole));

        // 一覧
        // SC-06 は運用者が「登録済みデータソースの有無」を判断する管理画面のため、後段障害を空一覧へ
        // 縮退させない（「未登録」と誤認させ重複登録を誘発するのを防ぐ）。非 2xx はそのまま伝播し、
        // 後段不達（HttpRequestException/TaskCanceledException）は 502 で可視化する（レビュー #169 指摘対応）。
        g.MapGet("/", async (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            var client = CreateForwardingClient(httpFactory, http);
            try
            {
                var resp = await client.GetAsync("/datasources", ct);
                if (!resp.IsSuccessStatusCode)
                    return Results.StatusCode((int)resp.StatusCode);
                var list = await resp.Content.ReadFromJsonAsync<List<DataSourceDto>>(ct);
                return Results.Ok(list ?? []);
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        }).WithName("BffDataSourceList").Produces<List<DataSourceDto>>();

        // 個別取得
        g.MapGet("/{id:guid}", async (Guid id, IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            var client = CreateForwardingClient(httpFactory, http);
            var resp = await client.GetAsync($"/datasources/{id}", ct);
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode((int)resp.StatusCode);
            var ds = await resp.Content.ReadFromJsonAsync<DataSourceDto>(ct);
            return ds is null ? Results.NotFound() : Results.Ok(ds);
        }).WithName("BffDataSourceGet").Produces<DataSourceDto>();

        // 登録（FR-01: 機密区分の既定属性はサービス側でフェイルセーフ補完される）
        g.MapPost("/", async (CreateDataSourceRequest req, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
        {
            var client = CreateForwardingClient(httpFactory, http);
            var resp = await client.PostAsJsonAsync("/datasources", req, ct);
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode((int)resp.StatusCode);
            var created = await resp.Content.ReadFromJsonAsync<DataSourceDto>(ct);
            return created is null
                ? Results.StatusCode(StatusCodes.Status502BadGateway)
                : Results.Created($"/bff/datasources/{created.Id}", created);
        }).WithName("BffDataSourceCreate").Produces<DataSourceDto>(StatusCodes.Status201Created);

        // FR-01, UC-04: 手動同期トリガ（実際の取得はポーリングジョブ／コネクタが行う）
        g.MapPost("/{id:guid}/sync", async (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
        {
            var client = CreateForwardingClient(httpFactory, http);
            var resp = await client.PostAsync($"/datasources/{id}/sync", content: null, ct);
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode((int)resp.StatusCode);
            // 後段は 202 Accepted + { fetchId, status } を返す。そのまま中継する。
            var body = await resp.Content.ReadAsStringAsync(ct);
            return Results.Content(body, "application/json", statusCode: (int)resp.StatusCode);
        }).WithName("BffDataSourceSync");

        // 無効化（論理削除）
        g.MapDelete("/{id:guid}", async (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
        {
            var client = CreateForwardingClient(httpFactory, http);
            var resp = await client.DeleteAsync($"/datasources/{id}", ct);
            return Results.StatusCode((int)resp.StatusCode);
        }).WithName("BffDataSourceDelete");

        return app;
    }

    // FR-05: 利用者の資格情報を後段へ引き継ぐ named client を生成する。
    private static HttpClient CreateForwardingClient(IHttpClientFactory httpFactory, HttpContext http)
    {
        var client = httpFactory.CreateClient("DataSourceService");
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
        return client;
    }

    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested;
}
