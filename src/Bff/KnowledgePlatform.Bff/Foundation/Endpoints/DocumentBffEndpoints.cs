using KnowledgePlatform.Bff.Foundation.Authz;
using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Ports.Storage;
using System.Net.Http.Json;

namespace KnowledgePlatform.Bff.Foundation.Endpoints;

// FR-06, UC-01/UC-03/UC-07, SC-03: 文書の閲覧（読み取り）を集約する BFF エンドポイント。
// 横断検索（/bff/search）と同じく「ABAC スコープ解決（AuthorizationService）→ 文書取得（DocumentService）」
// を集約する。利用者スコープに合致しない文書、および不在の文書はいずれも 404 で応答し、存在を秘匿する
// （deny-by-default・IADR-0009/IADR-0038。「拒否」と「不在」を区別しない）。
// 本文（正規化 Markdown）は ABAC 判定後にオブジェクトストレージ（storage://）からサーバサイドで取得する
// （WikiService の読み取り経路と同一。未配備・未設定時はプレースホルダ本文へ縮退）。
public static class DocumentBffEndpoints
{
    public static IEndpointRouteBuilder MapDocumentBffEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/bff/documents").WithTags("Documents BFF");

        // 一覧: 権限内の文書のみ返す（deny-by-default。権限外文書は列挙しない）。
        g.MapGet("/", async (
            IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            var scope = await BffScopeResolver.ResolveAsync(httpFactory, http, ct);
            if (scope is null)
                return Results.Ok(new List<DocumentDto>());

            var docs = await FetchListAsync(httpFactory, ct);
            var visible = docs.Where(d => BffScopeResolver.Matches(d.Attributes, scope)).ToList();
            return Results.Ok(visible);
        }).WithName("BffDocumentList").Produces<List<DocumentDto>>();

        // 詳細: スコープ外・不在ともに 404（存在秘匿）。
        g.MapGet("/{id:guid}", async (
            Guid id, IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            var doc = await FetchAuthorizedAsync(id, httpFactory, http, ct);
            return doc is null ? Results.NotFound() : Results.Ok(doc);
        }).WithName("BffDocumentDetail").Produces<DocumentDto>();

        // 版履歴: スコープ外・不在は 404。
        g.MapGet("/{id:guid}/versions", async (
            Guid id, IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            var doc = await FetchAuthorizedAsync(id, httpFactory, http, ct);
            if (doc is null)
                return Results.NotFound();

            var client = httpFactory.CreateClient("DocumentService");
            var versions = await client.GetFromJsonAsync<List<DocumentVersionDto>>(
                $"/documents/{id}/versions", ct);
            return Results.Ok(versions ?? []);
        }).WithName("BffDocumentVersions").Produces<List<DocumentVersionDto>>();

        // 本文（正規化 Markdown）: ABAC 判定後にオブジェクトストレージから読み取る。
        g.MapGet("/{id:guid}/content", async (
            Guid id, IHttpClientFactory httpFactory, IObjectStorageClient storage,
            HttpContext http, CancellationToken ct) =>
        {
            var doc = await FetchAuthorizedAsync(id, httpFactory, http, ct);
            if (doc is null)
                return Results.NotFound();

            var markdown = await ReadMarkdownAsync(storage, doc.MarkdownUri, doc.Title, ct);
            return Results.Ok(new DocumentContentDto(doc.Id, doc.Title, markdown, doc.MarkdownUri));
        }).WithName("BffDocumentContent").Produces<DocumentContentDto>();

        return app;
    }

    // 文書を取得し、利用者スコープに合致するときのみ返す（deny/不在/解決不能 → null＝404 秘匿）。
    private static async Task<DocumentDto?> FetchAuthorizedAsync(
        Guid id, IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
    {
        var scope = await BffScopeResolver.ResolveAsync(httpFactory, http, ct);
        if (scope is null)
            return null;

        var client = httpFactory.CreateClient("DocumentService");
        HttpResponseMessage resp;
        try
        {
            resp = await client.GetAsync($"/documents/{id}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }

        if (!resp.IsSuccessStatusCode)
            return null; // 404 等（不在）は秘匿し区別しない

        var doc = await resp.Content.ReadFromJsonAsync<DocumentDto>(ct);
        if (doc is null || !BffScopeResolver.Matches(doc.Attributes, scope))
            return null; // スコープ外は不在と同じ 404

        return doc;
    }

    private static async Task<List<DocumentDto>> FetchListAsync(
        IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("DocumentService");
        try
        {
            return await client.GetFromJsonAsync<List<DocumentDto>>("/documents", ct) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    // FR-06: 正規化 Markdown をオブジェクトストレージ（storage://）から取得する。ストレージ未配備・
    // URI 未設定時はプレースホルダ本文へ縮退する（WikiService.StorageMarkdownReader と同じ縮退方針）。
    private static async Task<string> ReadMarkdownAsync(
        IObjectStorageClient storage, string? markdownUri, string title, CancellationToken ct)
    {
        if (storage.CanResolve(markdownUri))
            return await storage.GetTextAsync(markdownUri!, ct);
        return $"# {title}\n\nコンテンツは {markdownUri ?? "(未設定)"} から取得します。";
    }
}
