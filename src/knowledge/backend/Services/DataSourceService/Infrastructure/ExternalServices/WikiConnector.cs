using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataSourceService.Domain;
using DataSourceService.Domain.Ports;

namespace DataSourceService.Infrastructure.ExternalServices;

// FR-01, UC-04, 09_datasource-connectors（優先2: Wiki）, IADR-0053:
// 既存社内Wiki を「設定駆動の汎用 REST 契約」で取り込むコネクタ。製品固有 Wiki（Confluence 等）は
// 本契約へアダプタで合わせるか、別途 IDataSourceConnector を追加する（プラグイン方式）。
//
// 契約:
//   ページ一覧（Discover）: GET {ConnectionUri}{listPath}（既定 /api/pages）
//     → JSON 配列 [{ id, title?, updatedAt(ISO8601), updatedBy? }]。updatedAt > since で増分（初回=全件）。
//   ページ本文（Fetch）:    GET {ConnectionUri}{contentPath}（既定 /api/pages/{id}/content、{id} 置換）
//     → 応答本文を原本バイト、content-type は応答ヘッダ→既定 text/markdown。
//   認証: Authorization: Bearer {Config["apiToken"]}（存在時。ログ出力しない）。
//   失敗: HTTP/JSON 失敗は例外を送出 → オーケストレータ（IADR-0051 決定3a）が watermark 非前進・継続失敗アラートに載せる。
//
// FR-05, UC-04, ADR-0036, ADR-0074, #752: **更新者（`updatedBy`）を運ぶ。**
//   計画 09_datasource-connectors §システム投入経路 は `owner` の既定を「ソース側の更新者・作成者を
//   利用者識別子へ解決して入れる」と定めるが、**従前この DTO は更新者を読んでいなかった**。
//   🔴 **この「REST 契約」は外部組織が持つものではなく、本リポジトリの自前 DTO である** ——
//   接続先は `ConnectionUri` ＋ `Config["listPath"]` で構成可能な汎用 JSON エンドポイントであり、
//   読む項目を決めているのはここである。したがって拡張に計画側の裁定は要らない。
//   項目名は `Config["updatedByField"]`（既定 `updatedBy`）で**構成可能**にする ——
//   実 Wiki 製品ごとに名前が違い（`lastModifiedBy` / `author` 等）、実装が 1 つに決め打てない。
//   **項目が無ければ運ばない**（`SourceUpdatedByOrigin.NotCarried`）。加算のみなので非破壊である。
public sealed class WikiConnector(IHttpClientFactory httpFactory, ILogger<WikiConnector> logger)
    : IDataSourceConnector
{
    public string SourceType => "wiki";

    private const string DefaultListPath = "/api/pages";
    private const string DefaultContentPath = "/api/pages/{id}/content";
    private const string DefaultContentType = "text/markdown";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SourceItem>> DiscoverAsync(
        DataSource source, DateTimeOffset? since, CancellationToken ct)
    {
        var baseUrl = BaseUrl(source);
        if (baseUrl is null)
        {
            // 接続先未設定は設定不備。例外にせず空列挙で縮退する（サイクルを止めない）。
            logger.LogWarning("WikiConnector: ConnectionUri が未設定のため空列挙します（source {Id}）", source.Id);
            return [];
        }

        using var client = CreateClient(source);
        var listUrl = baseUrl + Config(source, "listPath", DefaultListPath);
        // HTTP/JSON 失敗は例外を送出（ネットワーク断・4xx/5xx・不正 JSON）。呼び出し側が再試行を担保する。
        var pages = await client.GetFromJsonAsync<List<WikiPage>>(listUrl, JsonOptions, ct) ?? [];

        var updatedByField = Config(source, SourceUpdatedBy.FieldConfigKey, SourceUpdatedBy.DefaultField);
        var tally = new SourceUpdatedByTally();

        var items = new List<SourceItem>();
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(page.Id))
                continue;
            // 増分: 前回同期時刻（含む同時刻）以前は除外。since=null は初回全件。
            if (since is { } watermark && page.UpdatedAt <= watermark)
                continue;
            // #752: 更新者を読む。**由来（無い／空だった／読めない）を分類して集計する。**
            var updatedBy = SourceUpdatedBy.FromJson(page.Extra, updatedByField);
            tally.Add(updatedBy.Origin);
            // Path にはページ ID を載せる（Fetch で本文取得に用いる）。サイズは Wiki では不定のため 0。
            items.Add(new SourceItem(page.Id, page.UpdatedAt, 0, updatedBy.Value));
        }

        WarnOnUpdatedByAnomaly(tally, updatedByField, source);
        return items;
    }

    public async Task<RawContent> FetchAsync(DataSource source, SourceItem item, CancellationToken ct)
    {
        var baseUrl = BaseUrl(source)
            ?? throw new InvalidOperationException("WikiConnector: ConnectionUri が未設定です。");
        using var client = CreateClient(source);
        var contentPath = Config(source, "contentPath", DefaultContentPath)
            .Replace("{id}", Uri.EscapeDataString(item.Path), StringComparison.Ordinal);

        using var resp = await client.GetAsync(baseUrl + contentPath, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? DefaultContentType;
        return new RawContent(bytes, contentType);
    }

    // ---- helpers ---------------------------------------------------------

    // #752: 🔴 **「取れなかった」では鳴らさない。** 項目を構成していないのは正常な状態である。
    // 鳴らすのは「項目は在ったのに使えなかった」2 種だけで、1 サイクル 1 行に畳む。
    private void WarnOnUpdatedByAnomaly(SourceUpdatedByTally tally, string field, DataSource source)
    {
        if (!tally.HasAnomaly)
            return;

        logger.LogWarning(
            "WikiConnector: 更新者を読めなかったページがあります（source {Id}, 項目 '{Field}'）: "
            + "取得 {Carried} 件 / ソース側が空 {Blank} 件 / 文字列として読めない {Unreadable} 件 / 項目なし {Missing} 件",
            source.Id, field, tally.Carried, tally.BlankAtSource, tally.Unreadable, tally.NotCarried);
    }

    private HttpClient CreateClient(DataSource source)
    {
        var client = httpFactory.CreateClient("WikiConnector");
        var token = Config(source, "apiToken", string.Empty);
        if (!string.IsNullOrWhiteSpace(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // 接続先ベース URL（末尾スラッシュを除去。絶対 URL でなければ null＝縮退）。
    private static string? BaseUrl(DataSource source)
        => Uri.TryCreate(source.ConnectionUri, UriKind.Absolute, out _)
            ? source.ConnectionUri.TrimEnd('/')
            : null;

    private static string Config(DataSource source, string key, string fallback)
        => source.Config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    // 汎用 Wiki 契約のページ記述子（JSON web 既定でデシリアライズ）。
    //
    // #752: 更新者は**宣言済みプロパティにしない**。項目名が構成可能（`updatedByField`）である以上、
    // 名前を固定した受け皿を置くと既定名のときしか当たらない。未知項目をまとめて捕え、
    // 構成された名前で引く（`SourceUpdatedBy.FromJson`）。
    private sealed record WikiPage(string Id, string? Title, DateTimeOffset UpdatedAt)
    {
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
