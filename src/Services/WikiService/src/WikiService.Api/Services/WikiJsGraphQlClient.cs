using System.Net.Http.Json;
using System.Text.Json;

namespace WikiService.Api.Services;

// FR-13, UC-07, ADR-0011, IADR-0021: Wiki.js 管理 GraphQL API への同期クライアント（API push）。
//
// 冪等 upsert: path をキーに singleByPath で既存を引き、あれば pages.update、無ければ pages.create。
// 認証はサービスアカウントの API キー（Bearer）。秘密は環境変数/シークレット経由で注入しコミットしない。
//
// 注（IADR-0021）: Wiki.js 2.x の GraphQL スキーマ（バージョン差異あり）への結合が生じる。実際の
//   スキーマ整合・エラー時再送・レイテンシは稼働 Wiki.js での PoC 実測が必要。本実装は 2.x の
//   documented スキーマ（pages.singleByPath / pages.create / pages.update）に忠実に構成し、
//   スキーマ確定は PoC フォローで調整する。呼び出し側（DocumentSyncConsumer）は例外を送出させ、
//   MassTransit のリトライ/デッドレター（UseKnowledgePlatformRetry）へ委ねる。
public class WikiJsGraphQlClient(HttpClient http, ILogger<WikiJsGraphQlClient> logger) : IWikiJsClient
{
    private const string Locale = "ja";
    private const string Editor = "markdown";

    public async Task UpsertPageAsync(WikiJsPage page, CancellationToken ct = default)
    {
        var path = NormalizePath(page.Path);
        var existingId = await GetPageIdByPathAsync(path, ct);

        if (existingId is int id)
        {
            await UpdateAsync(id, path, page, ct);
            logger.LogInformation("Wiki.js page updated: path={Path} id={Id}", path, id);
        }
        else
        {
            await CreateAsync(path, page, ct);
            logger.LogInformation("Wiki.js page created: path={Path}", path);
        }
    }

    public async Task<string?> GetRenderedContentAsync(string path, CancellationToken ct = default)
    {
        const string query = """
            query ($path: String!, $locale: String!) {
              pages { singleByPath(path: $path, locale: $locale) { id render content } }
            }
            """;
        var data = await PostAsync(query, new { path = NormalizePath(path), locale = Locale }, ct);
        var single = data.GetProperty("pages").GetProperty("singleByPath");
        if (single.ValueKind == JsonValueKind.Null) return null;
        // render（HTML）を優先し、無ければ content（Markdown）を返す。
        return single.TryGetProperty("render", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : single.GetProperty("content").GetString();
    }

    private async Task<int?> GetPageIdByPathAsync(string path, CancellationToken ct)
    {
        const string query = """
            query ($path: String!, $locale: String!) {
              pages { singleByPath(path: $path, locale: $locale) { id } }
            }
            """;
        var data = await PostAsync(query, new { path, locale = Locale }, ct);
        var single = data.GetProperty("pages").GetProperty("singleByPath");
        return single.ValueKind == JsonValueKind.Null ? null : single.GetProperty("id").GetInt32();
    }

    private async Task CreateAsync(string path, WikiJsPage page, CancellationToken ct)
    {
        const string mutation = """
            mutation ($content: String!, $description: String!, $editor: String!, $isPublished: Boolean!,
                      $isPrivate: Boolean!, $locale: String!, $path: String!, $tags: [String]!, $title: String!) {
              pages { create(content: $content, description: $description, editor: $editor,
                             isPublished: $isPublished, isPrivate: $isPrivate, locale: $locale,
                             path: $path, tags: $tags, title: $title) {
                responseResult { succeeded errorCode message }
              } }
            }
            """;
        var data = await PostAsync(mutation, new
        {
            content = page.Markdown,
            description = "",
            editor = Editor,
            isPublished = true,
            isPrivate = false,
            locale = Locale,
            path,
            tags = page.Tags,
            title = page.Title,
        }, ct);
        EnsureSucceeded(data.GetProperty("pages").GetProperty("create"), "create", path);
    }

    private async Task UpdateAsync(int id, string path, WikiJsPage page, CancellationToken ct)
    {
        const string mutation = """
            mutation ($id: Int!, $content: String!, $editor: String!, $isPublished: Boolean!,
                      $isPrivate: Boolean!, $locale: String!, $path: String!, $tags: [String]!, $title: String!) {
              pages { update(id: $id, content: $content, editor: $editor, isPublished: $isPublished,
                             isPrivate: $isPrivate, locale: $locale, path: $path, tags: $tags, title: $title) {
                responseResult { succeeded errorCode message }
              } }
            }
            """;
        var data = await PostAsync(mutation, new
        {
            id,
            content = page.Markdown,
            editor = Editor,
            isPublished = true,
            isPrivate = false,
            locale = Locale,
            path,
            tags = page.Tags,
            title = page.Title,
        }, ct);
        EnsureSucceeded(data.GetProperty("pages").GetProperty("update"), "update", path);
    }

    // GraphQL POST を実行し data を返す。transport 失敗・GraphQL errors は例外（→ MassTransit リトライ）。
    private async Task<JsonElement> PostAsync(string query, object variables, CancellationToken ct)
    {
        var resp = await http.PostAsJsonAsync("", new { query, variables }, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);

        if (doc.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            var message = errors[0].TryGetProperty("message", out var m) ? m.GetString() : "unknown";
            throw new WikiJsSyncException($"Wiki.js GraphQL error: {message}");
        }
        return doc.GetProperty("data");
    }

    // responseResult.succeeded=false は同期失敗として例外化する（リトライ対象）。
    private static void EnsureSucceeded(JsonElement operation, string op, string path)
    {
        var result = operation.GetProperty("responseResult");
        if (result.GetProperty("succeeded").GetBoolean()) return;
        var code = result.TryGetProperty("errorCode", out var c) ? c.ToString() : "?";
        var message = result.TryGetProperty("message", out var m) ? m.GetString() : "?";
        throw new WikiJsSyncException($"Wiki.js pages.{op} failed for '{path}' (code={code}): {message}");
    }

    // Wiki.js のパスは先頭スラッシュを持たない相対形（例 doc/<guid>）。
    private static string NormalizePath(string path) => path.TrimStart('/');
}

// Wiki.js 同期の失敗（transport/GraphQL/業務エラー）。MassTransit のリトライ・デッドレターへ委ねる。
public class WikiJsSyncException(string message) : Exception(message);
