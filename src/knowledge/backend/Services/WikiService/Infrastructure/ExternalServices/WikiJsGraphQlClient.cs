using WikiService.Domain.Ports;
using System.Net.Http.Json;
using System.Text.Json;

namespace WikiService.Infrastructure.ExternalServices;

// FR-13, UC-07, ADR-0011, IADR-0021: Wiki.js 管理 GraphQL API への同期クライアント（API push）。
//
// 冪等 upsert: path をキーに singleByPath で既存を引き、あれば pages.update、無ければ pages.create。
// 認証はサービスアカウントの API キー（Bearer）。秘密は環境変数/シークレット経由で注入しコミットしない。
//
// 注（IADR-0021）: Wiki.js 2.x の GraphQL スキーマ（バージョン差異あり）への結合が生じる。実際の
//   スキーマ整合・エラー時再送・レイテンシは稼働 Wiki.js での PoC 実測が必要。本実装は 2.x の
//   documented スキーマ（pages.singleByPath / pages.create / pages.update）に忠実に構成し、
//   スキーマ確定は PoC フォローで調整する。呼び出し側（DocumentSyncConsumer）は例外を送出させ、
//   ブローカのリトライ/デッドレター（Wolverine の UsePlatformMessagingDefaults）へ委ねる。
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
            await UpdateAsync(id, path, page, isPublished: true, ct);
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
        var single = await QuerySingleByPathAsync(query, NormalizePath(path), ct);
        if (single is not JsonElement page) return null;
        // render（HTML）を優先し、無ければ content（Markdown）を返す。
        return page.TryGetProperty("render", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : page.GetProperty("content").GetString();
    }

    // Issue #88: アーカイブ（非公開化）。unpublish で Wiki.js 上から不可視にする。
    // 未存在は冪等に成功扱い（再配信・未同期 ID に対して安全）。
    //
    // 実測（Wiki.js 2.5.314・Issue #88 PoC）に基づく制約:
    //   - content を省略した部分 update は 6004（Page content cannot be empty）で失敗する。
    //   - tags を省略すると 'map' エラーになり、しかも部分適用される（非トランザクショナル）。
    //   → 現在の content/title/tags を取得してから、UpdateAsync と同じ全項目シェイプで送る。
    //   - update は isPrivate の変更を無視する（create 時のみ有効）。非公開化の実効手段は
    //     isPublished=false（unpublish）であり、isPrivate は将来バージョンでの適用を期待して送るのみ。
    public async Task ArchivePageAsync(string path, CancellationToken ct = default)
    {
        var normalized = NormalizePath(path);
        const string query = """
            query ($path: String!, $locale: String!) {
              pages { singleByPath(path: $path, locale: $locale) { id content title tags { tag } } }
            }
            """;
        var single = await QuerySingleByPathAsync(query, normalized, ct);
        if (single is not JsonElement page)
        {
            logger.LogInformation("Wiki.js page archive skipped (not found): path={Path}", normalized);
            return;
        }

        var id = page.GetProperty("id").GetInt32();
        var tags = page.GetProperty("tags").EnumerateArray()
            .Select(t => t.GetProperty("tag").GetString() ?? "")
            .ToArray();
        var current = new WikiJsPage(
            normalized,
            page.GetProperty("title").GetString() ?? "",
            page.GetProperty("content").GetString() ?? "",
            tags,
            IsPrivate: true);
        await UpdateAsync(id, normalized, current, isPublished: false, ct);
        logger.LogInformation("Wiki.js page archived (unpublished): path={Path} id={Id}", normalized, id);
    }

    // Issue #88: 実体撤去（pages.delete）。社内文書の外部システム残存防止。未存在は冪等に成功扱い。
    public async Task DeletePageAsync(string path, CancellationToken ct = default)
    {
        var normalized = NormalizePath(path);
        var existingId = await GetPageIdByPathAsync(normalized, ct);
        if (existingId is not int id)
        {
            logger.LogInformation("Wiki.js page delete skipped (not found): path={Path}", normalized);
            return;
        }

        const string mutation = """
            mutation ($id: Int!) {
              pages { delete(id: $id) {
                responseResult { succeeded errorCode message }
              } }
            }
            """;
        var data = await PostAsync(mutation, new { id }, ct);
        EnsureSucceeded(data.GetProperty("pages").GetProperty("delete"), "delete", normalized);
        logger.LogInformation("Wiki.js page deleted: path={Path} id={Id}", normalized, id);
    }

    private async Task<int?> GetPageIdByPathAsync(string path, CancellationToken ct)
    {
        const string query = """
            query ($path: String!, $locale: String!) {
              pages { singleByPath(path: $path, locale: $locale) { id } }
            }
            """;
        var single = await QuerySingleByPathAsync(query, path, ct);
        return single is JsonElement page ? page.GetProperty("id").GetInt32() : null;
    }

    // 実測（Wiki.js 2.5.314）: 未存在の singleByPath は data=null に加えて GraphQL errors
    // （PageNotFound 6003）を返す。これを例外にすると新規ページの create に到達できないため、
    // 6003 は「未存在（null）」として扱う。その他のエラーは例外（→ ブローカのリトライ）。
    private async Task<JsonElement?> QuerySingleByPathAsync(string query, string path, CancellationToken ct)
    {
        var envelope = await PostEnvelopeAsync(query, new { path, locale = Locale }, ct);
        if (IsPageNotFound(envelope)) return null;

        var single = GetDataOrThrow(envelope).GetProperty("pages").GetProperty("singleByPath");
        return single.ValueKind == JsonValueKind.Null ? null : single;
    }

    private static bool IsPageNotFound(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var error in errors.EnumerateArray())
        {
            if (error.TryGetProperty("extensions", out var ext)
                && ext.TryGetProperty("exception", out var ex)
                && ex.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.GetInt32() == 6003)
                return true;
        }
        return false;
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
            // 多層防御（ADR-0011/IADR-0021）: 機密区分由来の粗粒度な非公開設定。public 以外は非公開。
            isPrivate = page.IsPrivate,
            locale = Locale,
            path,
            tags = page.Tags,
            title = page.Title,
        }, ct);
        EnsureSucceeded(data.GetProperty("pages").GetProperty("create"), "create", path);
    }

    // 実測（Wiki.js 2.5.314）: update は content/tags を省略すると失敗（6004 / 'map' エラー）し、
    // かつ部分適用される（非トランザクショナル）ため、常に全項目シェイプで送る。
    private async Task UpdateAsync(int id, string path, WikiJsPage page, bool isPublished, CancellationToken ct)
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
            isPublished,
            // 多層防御（ADR-0011/IADR-0021）: 機密区分由来の粗粒度な非公開設定。public 以外は非公開。
            // 注（実測）: Wiki.js 2.5 の update は isPrivate 変更を無視する（create 時のみ有効）。
            isPrivate = page.IsPrivate,
            locale = Locale,
            path,
            tags = page.Tags,
            title = page.Title,
        }, ct);
        EnsureSucceeded(data.GetProperty("pages").GetProperty("update"), "update", path);
    }

    // GraphQL POST を実行し data を返す。transport 失敗・GraphQL errors は例外（→ ブローカのリトライ）。
    private async Task<JsonElement> PostAsync(string query, object variables, CancellationToken ct)
        => GetDataOrThrow(await PostEnvelopeAsync(query, variables, ct));

    // GraphQL POST を実行し応答全体（data + errors）を返す。transport 失敗のみ例外。
    private async Task<JsonElement> PostEnvelopeAsync(string query, object variables, CancellationToken ct)
    {
        var resp = await http.PostAsJsonAsync("", new { query, variables }, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static JsonElement GetDataOrThrow(JsonElement envelope)
    {
        if (envelope.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            var message = errors[0].TryGetProperty("message", out var m) ? m.GetString() : "unknown";
            throw new WikiJsSyncException($"Wiki.js GraphQL error: {message}");
        }
        return envelope.GetProperty("data");
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

// Wiki.js 同期の失敗（transport/GraphQL/業務エラー）。ブローカ（Wolverine）のリトライ・デッドレターへ委ねる。
public class WikiJsSyncException(string message) : Exception(message);
