using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using System.Net.Http.Json;

namespace Platform.Bff.Foundation.Endpoints;

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

        // ---- SC-05, FR-06, UC-03, IADR-0041: 文書管理（書き込み） ----
        // 書き込みは管理系のため platform-admin/operator に限定する（読み取りは SC-02/03 用に無制限）。
        // 既存文書への操作（更新・メタ更新・公開・アーカイブ・削除）は、対象が利用者スコープ内であることを
        // 先に確認し、スコープ外・不在はいずれも 404 で秘匿する（閲覧できない文書は変更もできない）。
        var write = app.MapGroup("/bff/documents")
            .WithTags("Documents BFF")
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        // 新規作成: スコープ解決済み（権限あり）の管理者が作成する。検証（タイトル必須=400）は透過する。
        write.MapPost("/", async (DocumentCreateRequest req, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
        {
            var scope = await BffScopeResolver.ResolveAsync(httpFactory, http, ct);
            if (scope is null)
                return Results.Forbid(); // 許可ポリシー無し＝作成不可（deny-by-default）

            var client = Forwarding(httpFactory, http);
            var resp = await client.PostAsJsonAsync("/documents", req, ct);
            return await RelayAsync(resp, ct);
        }).WithName("BffDocumentCreate").Produces<DocumentDto>(StatusCodes.Status201Created);

        // 更新（楽観ロック。ExpectedVersion 不一致=409 透過）。
        write.MapPut("/{id:guid}", (Guid id, DocumentUpdateRequest req, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardIfInScope(id, HttpMethod.Put, $"/documents/{id}", req, httpFactory, http, ct))
            .WithName("BffDocumentUpdate");

        // 公開（取り込み・Wiki 同期をトリガ）。
        write.MapPost("/{id:guid}/publish", (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardIfInScope(id, HttpMethod.Post, $"/documents/{id}/publish", null, httpFactory, http, ct))
            .WithName("BffDocumentPublish");

        // アーカイブ（非公開化）。
        write.MapPost("/{id:guid}/archive", (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardIfInScope(id, HttpMethod.Post, $"/documents/{id}/archive", null, httpFactory, http, ct))
            .WithName("BffDocumentArchive");

        // 削除（下流の Wiki 同期へ伝播）。
        write.MapDelete("/{id:guid}", (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardIfInScope(id, HttpMethod.Delete, $"/documents/{id}", null, httpFactory, http, ct))
            .WithName("BffDocumentDelete");

        return app;
    }

    // SC-05: 対象文書が利用者スコープ内のときのみ後段へ書き込みを転送する。スコープ外・不在は 404 秘匿。
    // 検証（400）・楽観ロック競合（409）は後段の応答を透過する。
    // NFR/#179・IADR-0045: このプリフライト GET（スコープ確認）を「後段に認可がある（IADR-0044）から冗長」
    // として削除してはならない。IADR-0044 が後段に課したのはロール認可のみで、文書単位の ABAC スコープ
    // 照合はこの BFF が唯一の実施点（IADR-0041）。削除するとスコープ外の admin/operator が閲覧不可の文書を
    // 変更でき、存在秘匿（IADR-0009）も破れる。往復削減は実測で正当化された上で IADR-0045 の代替案に従う。
    private static async Task<IResult> ForwardIfInScope(
        Guid id, HttpMethod method, string path, object? body,
        IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
    {
        var doc = await FetchAuthorizedAsync(id, httpFactory, http, ct);
        if (doc is null)
            return Results.NotFound(); // 閲覧できない文書は変更もできない（存在秘匿）

        var client = Forwarding(httpFactory, http);
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = JsonContent.Create(body, body.GetType());

        var resp = await client.SendAsync(req, ct);
        return await RelayAsync(resp, ct);
    }

    // 後段の応答（status・content-type・本文）をそのまま返す（検証 400・競合 409・不在 404 を保つ）。
    private static async Task<IResult> RelayAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return Results.NoContent();
        var content = await resp.Content.ReadAsStringAsync(ct);
        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        return Results.Content(content, contentType, statusCode: (int)resp.StatusCode);
    }

    // FR-05: 利用者の資格情報を後段へ引き継ぐ DocumentService クライアントを生成する。
    private static HttpClient Forwarding(IHttpClientFactory httpFactory, HttpContext http)
    {
        var client = httpFactory.CreateClient("DocumentService");
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
        return client;
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

// SC-05, FR-06, UC-03: 文書管理の書き込みリクエスト（BFF→DocumentService。JSON 互換）。
public record DocumentCreateRequest(
    string Title,
    string? OriginalUri,
    string? ContentType,
    Dictionary<string, string>? Attributes,
    List<string>? Tags);

public record DocumentUpdateRequest(
    string Title,
    Dictionary<string, string>? Attributes,
    List<string>? Tags,
    int? ExpectedVersion = null,
    string? ChangeNote = null);
