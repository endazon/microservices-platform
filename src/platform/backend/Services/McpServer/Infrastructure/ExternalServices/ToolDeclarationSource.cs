using System.Text.Json;
using McpServer.Domain;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace McpServer.Infrastructure.ExternalServices;

// FR-16, FR-15, ADR-0024 §2: 各サービスの自己申告（`GET /internal/mcp-tools`）を集める口。
// FR-15 の `GET /internal/introspection` と同じ規約系に置く（メッシュ内部限定）。
public interface IToolDeclarationSource
{
    Task<IReadOnlyList<ServiceToolDeclarations>> CollectAsync(CancellationToken ct);
}

// FR-16, ADR-0024 §2: HTTP でメッシュ内部の各サービスから自己申告を集める実装。
// ［2026-09-26 追記 / #1515］本番の収集器は `ToolDeclarationSource`（宛先ごとに REST か gRPC を選ぶ）であり、
// 本クラスはその REST 側（1 宛先ぶんの `CollectOneAsync`）を担う。並走中の正は REST（IADR-0379 決定 5）。
//
// 収集先は構成（`Mcp:Services:<サービス名>` = ベース URL）で与える。**コードへサービス名を
// 書かない** —— 新サービスがツールを公開する手順を「自サービスに /internal/mcp-tools を実装」
// ＋「公開構成に追記」の 2 手順に保つため（ADR-0024 §決定「コア改修不要の追従」）。
public sealed class HttpToolDeclarationSource(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<HttpToolDeclarationSource> logger) : IToolDeclarationSource
{
    public const string ToolsPath = "/internal/mcp-tools";
    public const string ServicesSection = "Mcp:Services";

    // IADR-0462（2026-09-26 追記 / #1515）: 名前付きクライアントの名前。gRPC 収集の期限は
    // この HttpClient の `Timeout` を引く（輸送ごとに別の値を持たない）。
    public const string HttpClientName = nameof(HttpToolDeclarationSource);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // REST だけで収集する（宛先 = `Mcp:Services`）。本番は `ToolDeclarationSource` が宛先ごとに輸送を選ぶ。
    public async Task<IReadOnlyList<ServiceToolDeclarations>> CollectAsync(CancellationToken ct)
    {
        var collected = new List<ServiceToolDeclarations>();
        foreach (var (service, baseUrl) in ConfiguredServices(configuration))
        {
            if (await CollectOneAsync(service, baseUrl, ct) is { } declared)
                collected.Add(declared);
        }
        return collected;
    }

    // 1 宛先ぶんを REST で収集する。得られなければ null（申告なし）。
    public async Task<ServiceToolDeclarations?> CollectOneAsync(string service, string baseUrl, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            var url = baseUrl.TrimEnd('/') + ToolsPath;
            var json = await client.GetStringAsync(url, ct);
            return JsonSerializer.Deserialize<ServiceToolDeclarations>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 到達できないサービスは「申告なし」として扱う。公開構成が要求していれば
            // ToolCatalog が構成ドリフトとして警告する（ADR-0024 §5）。
            // **推測で公開しない**（既定は非公開）。
            logger.LogWarning(ex,
                "Failed to collect MCP tool declarations from {Service}", service);
            return null;
        }
    }

    // REST の宛先（値が空の項目を除く。構成の順序を保つ）。
    public static IReadOnlyList<(string Service, string BaseUrl)> ConfiguredServices(IConfiguration configuration) =>
        configuration.GetSection(ServicesSection).GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => (c.Key, c.Value!))
            .ToList();
}

// FR-16, NFR-16, ADR-0024 §2・§5, ADR-0029, ADR-0075, IADR-0379 決定 5, IADR-0462（2026-09-26 追記 / #1515, #1255 経路 ④-a）:
// MCP サーバーが使う申告の収集器。**宛先ごとに輸送を選ぶ**（構成情報 API の `EffectiveConfigCollector` と同じ形）。
//
// 🔴 **扇形の経路は宛先単位で移る。** 宛先の集合 = `Mcp:Services` と `Mcp:GrpcServices` のキーの和。
// `Mcp:GrpcServices` に（空でない）アドレスが在る宛先は gRPC、それ以外は REST（両方に在れば gRPC）。
// **並走中の正は REST**（IADR-0379 決定 5）—— 戻すのは gRPC 側の項目を消すだけ。
// 収集の順序と失敗の扱い（申告なしへ畳む）は REST だけの収集と同じ。
public sealed class ToolDeclarationSource : IToolDeclarationSource
{
    private readonly HttpToolDeclarationSource _http;
    private readonly GrpcToolDeclarationCollector? _grpc;
    private readonly IConfiguration _configuration;

    public ToolDeclarationSource(
        HttpToolDeclarationSource http,
        IConfiguration configuration,
        GrpcToolDeclarationCollector? grpc = null)
    {
        _http = http;
        _grpc = grpc;
        _configuration = configuration;

        // 🔴 gRPC の宛先が構成されているのに gRPC の収集器が居ないのは登録の誤りである。
        // 黙って REST へ倒すと「gRPC へ移したつもりで REST のまま」になり、REST の口を退役させた
        // 段で初めて「申告なし」として現れる（しかも推測で公開しないので、ツールが静かに消える）。起動の時点で落とす。
        if (_grpc is null && GrpcToolDeclarationCollector.ConfiguredTargets(configuration).Count > 0)
            throw new InvalidOperationException(
                $"{GrpcToolDeclarationCollector.GrpcServicesSection} が構成されていますが gRPC の収集器が登録されていません"
                + "（AddMcpToolDeclarationSources を経由せずに ToolDeclarationSource を組み立てていないか確かめること）。");
    }

    public async Task<IReadOnlyList<ServiceToolDeclarations>> CollectAsync(CancellationToken ct)
    {
        var grpcTargets = GrpcToolDeclarationCollector.ConfiguredTargets(_configuration);
        var restTargets = HttpToolDeclarationSource.ConfiguredServices(_configuration);
        var targets = restTargets.Select(r => r.Service)
            .Concat(grpcTargets.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var restByService = restTargets.ToDictionary(r => r.Service, r => r.BaseUrl, StringComparer.Ordinal);

        // 逐次に収集する（REST だけの収集と同じ。起動時＋定期の背景処理であり応答時間を待つ利用者は居ない）。
        var collected = new List<ServiceToolDeclarations>();
        foreach (var service in targets)
        {
            var declared = grpcTargets.TryGetValue(service, out var address)
                ? await _grpc!.CollectOneAsync(service, address, ct)
                : await _http.CollectOneAsync(service, restByService[service], ct);
            if (declared is not null)
                collected.Add(declared);
        }
        return collected;
    }
}

// FR-16, NFR-16, IADR-0462（2026-09-26 追記 / #1515）: 申告の収集器の登録。
public static class ToolDeclarationSourceExtensions
{
    // 🔴 gRPC の収集器と s2s トークンの発行側は `Mcp:GrpcServices` が構成されたときだけ登録する
    // （無い配備は従来どおり REST だけで収集し、この経路のために資格情報を要求しない）。
    public static IServiceCollection AddMcpToolDeclarationSources(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(HttpToolDeclarationSource.HttpClientName);
        services.AddScoped<HttpToolDeclarationSource>();
        if (GrpcToolDeclarationCollector.ConfiguredTargets(configuration).Count > 0)
        {
            services.AddPlatformServiceToken(configuration);
            services.AddSingleton<GrpcToolDeclarationCollector>();
        }
        services.AddScoped<IToolDeclarationSource, ToolDeclarationSource>();
        return services;
    }
}

// FR-16, ADR-0024 §2: 起動時＋定期（既定 5 分）に自己申告を収集し、公開構成と突合する。
public sealed class ToolCatalogRefresher(
    IServiceProvider services,
    ToolCatalog catalog,
    ToolPublicationConfigLoader loader,
    IConfiguration configuration,
    ILogger<ToolCatalogRefresher> logger) : BackgroundService
{
    public const string IntervalKey = "Mcp:RefreshIntervalSeconds";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue<int?>(IntervalKey) ?? 300;
        var interval = TimeSpan.FromSeconds(Math.Max(seconds, 10));

        while (!stoppingToken.IsCancellationRequested)
        {
            // 🔴 構成の破損（検証失敗）と、収集の一時失敗は別物である（#445 レビュー指摘）。
            // 前者は再試行しても直らず、握り潰すと「公開されているつもりの公開されていない」状態が
            // エラーログだけを吐きながら継続する。ホストを止めて気づかせる
            // （BackgroundServiceExceptionBehavior の既定は StopHost）。
            ToolPublicationConfig published;
            try
            {
                published = loader.Load();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogCritical(ex, "MCP publication config is invalid; stopping host");
                throw;
            }

            // 後者（自己申告の収集）は到達不能なサービスがあり得るため、次の周期へ持ち越す。
            try
            {
                using var scope = services.CreateScope();
                var source = scope.ServiceProvider.GetRequiredService<IToolDeclarationSource>();
                catalog.Refresh(published, await source.CollectAsync(stoppingToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "MCP tool catalog refresh failed");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
