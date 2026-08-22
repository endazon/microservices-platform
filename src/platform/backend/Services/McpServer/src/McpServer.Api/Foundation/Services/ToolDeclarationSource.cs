using System.Text.Json;
using McpServer.Api.Foundation.Contracts;

namespace McpServer.Api.Foundation.Services;

// FR-16, FR-15, ADR-0024 §2: 各サービスの自己申告（`GET /internal/mcp-tools`）を集める口。
// FR-15 の `GET /internal/introspection` と同じ規約系に置く（メッシュ内部限定）。
public interface IToolDeclarationSource
{
    Task<IReadOnlyList<ServiceToolDeclarations>> CollectAsync(CancellationToken ct);
}

// FR-16, ADR-0024 §2: HTTP でメッシュ内部の各サービスから自己申告を集める既定実装。
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ServiceToolDeclarations>> CollectAsync(CancellationToken ct)
    {
        var services = configuration.GetSection(ServicesSection).GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .ToList();

        var collected = new List<ServiceToolDeclarations>();
        foreach (var service in services)
        {
            try
            {
                var client = httpClientFactory.CreateClient(nameof(HttpToolDeclarationSource));
                var url = service.Value!.TrimEnd('/') + ToolsPath;
                var json = await client.GetStringAsync(url, ct);
                var declared = JsonSerializer.Deserialize<ServiceToolDeclarations>(json, JsonOptions);
                if (declared is not null) collected.Add(declared);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 到達できないサービスは「申告なし」として扱う。公開構成が要求していれば
                // ToolCatalog が構成ドリフトとして警告する（ADR-0024 §5）。
                // **推測で公開しない**（既定は非公開）。
                logger.LogWarning(ex,
                    "Failed to collect MCP tool declarations from {Service}", service.Key);
            }
        }
        return collected;
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
