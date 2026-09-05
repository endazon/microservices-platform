using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataSourceService.Domain;
using DataSourceService.Domain.Ports;

namespace DataSourceService.Infrastructure.ExternalServices;

// FR-01, UC-04, 09_datasource-connectors（優先3: SaaS）, IADR-0054:
// SaaS を「設定駆動の汎用 REST 契約」で取り込むコネクタ。カーソルページングと HTTP 429 バックオフに対応する。
// 製品固有 SaaS（Salesforce/Notion/Slack 等）は本契約へアダプタで合わせるか、別途コネクタを追加する（プラグイン方式）。
//
// 契約:
//   一覧（Discover・ページング）: GET {ConnectionUri}{listPath}（既定 /api/items）を nextCursor が尽きるまで
//     ?{cursorParam}={cursor} でたどる。応答 { items:[{ id, title?, updatedAt, updatedBy? }], nextCursor? }。updatedAt>since で増分。
//   本文（Fetch）: GET {ConnectionUri}{contentPath}（既定 /api/items/{id}、{id} 置換）→ 本文バイト＋content-type。
//   認証: Authorization: Bearer {Config["apiToken"]}（ログ非出力）。
//   レート制限: HTTP 429 を Retry-After（秒/日時）で待機し再試行。無ければ指数バックオフ。maxRetries 超過は例外送出。
//   失敗: 例外送出 → オーケストレータ（IADR-0051 決定3a）が watermark 非前進・継続失敗アラートに載せる。
//
// FR-05, UC-04, ADR-0036, ADR-0074, #752: **更新者（`updatedBy`）を運ぶ。** 項目名は
//   `Config["updatedByField"]`（既定 `updatedBy`）で構成可能。🔴 **この「REST 契約」は外部組織が
//   持つものではなく本リポジトリの自前 DTO であり**、拡張に計画側の裁定は要らない（wiki と同型）。
//   項目が無ければ運ばない（加算のみ＝非破壊）。由来の分類は `SourceUpdatedBy`。
public sealed class SaaSConnector(IHttpClientFactory httpFactory, ILogger<SaaSConnector> logger)
    : IDataSourceConnector
{
    public string SourceType => "saas";

    private const string DefaultListPath = "/api/items";
    private const string DefaultContentPath = "/api/items/{id}";
    private const string DefaultCursorParam = "cursor";
    private const string DefaultContentType = "text/markdown";
    private const int MaxPages = 1000;       // 無限ループ防止の安全上限
    private const int DefaultMaxRetries = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SourceItem>> DiscoverAsync(
        DataSource source, DateTimeOffset? since, CancellationToken ct)
    {
        var baseUrl = BaseUrl(source);
        if (baseUrl is null)
        {
            logger.LogWarning("SaaSConnector: ConnectionUri が未設定のため空列挙します（source {Id}）", source.Id);
            return [];
        }

        using var client = CreateClient(source);
        var listPath = Config(source, "listPath", DefaultListPath);
        var cursorParam = Config(source, "cursorParam", DefaultCursorParam);
        var maxRetries = ConfigInt(source, "maxRetries", DefaultMaxRetries);
        var updatedByField = Config(source, SourceUpdatedBy.FieldConfigKey, SourceUpdatedBy.DefaultField);
        var tally = new SourceUpdatedByTally();

        var items = new List<SourceItem>();
        string? cursor = null;
        for (var page = 0; page < MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = baseUrl + listPath;
            if (!string.IsNullOrEmpty(cursor))
                url += (listPath.Contains('?') ? "&" : "?") + $"{cursorParam}={Uri.EscapeDataString(cursor)}";

            var pageResult = await GetJsonWithRetryAsync<SaaSPage>(client, url, maxRetries, ct);
            foreach (var it in pageResult?.Items ?? [])
            {
                if (string.IsNullOrWhiteSpace(it.Id))
                    continue;
                if (since is { } watermark && it.UpdatedAt <= watermark)
                    continue;
                // #752: 更新者を読む。**由来（無い／空だった／読めない）を分類して集計する。**
                var updatedBy = SourceUpdatedBy.FromJson(it.Extra, updatedByField);
                tally.Add(updatedBy.Origin);
                items.Add(new SourceItem(it.Id, it.UpdatedAt, 0, updatedBy.Value));
            }

            cursor = pageResult?.NextCursor;
            if (string.IsNullOrEmpty(cursor))
                break; // 最終ページ
        }

        // 安全上限（MaxPages）に達してもカーソルが尽きていない＝一部ページで打ち切った。サイレント欠落を避け警告する。
        if (!string.IsNullOrEmpty(cursor))
            logger.LogWarning(
                "SaaSConnector: ページング安全上限 {MaxPages} に達したため打ち切りました。一部データが未取得の可能性があります（source {Id}）",
                MaxPages, source.Id);

        // #752: 🔴 **「取れなかった」では鳴らさない**（項目を構成していないのは正常な状態である）。
        // 鳴らすのは「項目は在ったのに使えなかった」2 種だけで、1 サイクル 1 行に畳む。
        if (tally.HasAnomaly)
            logger.LogWarning(
                "SaaSConnector: 更新者を読めなかった項目があります（source {Id}, 項目 '{Field}'）: "
                + "取得 {Carried} 件 / ソース側が空 {Blank} 件 / 文字列として読めない {Unreadable} 件 / 項目なし {Missing} 件",
                source.Id, updatedByField, tally.Carried, tally.BlankAtSource, tally.Unreadable, tally.NotCarried);

        return items;
    }

    public async Task<RawContent> FetchAsync(DataSource source, SourceItem item, CancellationToken ct)
    {
        var baseUrl = BaseUrl(source)
            ?? throw new InvalidOperationException("SaaSConnector: ConnectionUri が未設定です。");
        using var client = CreateClient(source);
        var maxRetries = ConfigInt(source, "maxRetries", DefaultMaxRetries);
        var contentPath = Config(source, "contentPath", DefaultContentPath)
            .Replace("{id}", Uri.EscapeDataString(item.Path), StringComparison.Ordinal);

        return await GetBytesWithRetryAsync(client, baseUrl + contentPath, maxRetries, ct);
    }

    // ---- HTTP（レート制限リトライ付き） ---------------------------------

    private async Task<T?> GetJsonWithRetryAsync<T>(
        HttpClient client, string url, int maxRetries, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var resp = await client.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxRetries)
            {
                await BackoffAsync(resp, attempt, ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        }
    }

    private async Task<RawContent> GetBytesWithRetryAsync(
        HttpClient client, string url, int maxRetries, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var resp = await client.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxRetries)
            {
                await BackoffAsync(resp, attempt, ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? DefaultContentType;
            return new RawContent(bytes, contentType);
        }
    }

    // 429 バックオフ: Retry-After（秒/日時）を尊重し、無ければ指数バックオフ（上限 2s）。
    private async Task BackoffAsync(HttpResponseMessage resp, int attempt, CancellationToken ct)
    {
        var retryAfter = resp.Headers.RetryAfter;
        var wait = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
            ?? TimeSpan.FromMilliseconds(Math.Min(2000, 200 * Math.Pow(2, attempt)));
        if (wait < TimeSpan.Zero)
            wait = TimeSpan.Zero;

        logger.LogWarning(
            "SaaSConnector: レート制限(429)。{WaitMs}ms 待機して再試行します（attempt {Attempt}）",
            wait.TotalMilliseconds, attempt + 1);
        await Task.Delay(wait, ct);
    }

    // ---- helpers ---------------------------------------------------------

    private HttpClient CreateClient(DataSource source)
    {
        var client = httpFactory.CreateClient("SaaSConnector");
        var token = Config(source, "apiToken", string.Empty);
        if (!string.IsNullOrWhiteSpace(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string? BaseUrl(DataSource source)
        => Uri.TryCreate(source.ConnectionUri, UriKind.Absolute, out _)
            ? source.ConnectionUri.TrimEnd('/')
            : null;

    private static string Config(DataSource source, string key, string fallback)
        => source.Config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    private static int ConfigInt(DataSource source, string key, int fallback)
        => source.Config.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : fallback;

    // 汎用 SaaS 契約: ページ応答（items＋nextCursor）とページ項目。
    private sealed record SaaSPage(List<SaaSItem>? Items, string? NextCursor);

    // #752: 更新者は**宣言済みプロパティにしない**（項目名が構成可能なので名前を固定できない）。
    // 未知項目をまとめて捕え、構成された名前で引く（`SourceUpdatedBy.FromJson`。wiki と同型）。
    private sealed record SaaSItem(string Id, string? Title, DateTimeOffset UpdatedAt)
    {
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
