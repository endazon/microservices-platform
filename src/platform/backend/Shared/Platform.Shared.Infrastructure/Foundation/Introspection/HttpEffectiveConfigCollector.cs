using System.Net.Http.Json;
using Platform.Shared.Contracts.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Shared.Infrastructure.Foundation.Introspection;

// FR-15: 設定済みサービスの自己申告エンドポイントを HTTP で収集する実装。
// メッシュ内部通信（IADR-0017 ネットワーク分離 / IADR-0026 mTLS）を前提とし、
// 到達不能なサービスは UnreachableServices に記録して呼び出し側（ドリフト検出）の
// 誤検知抑制に用いる（適用漏れと到達不能を区別する）。
public sealed class HttpEffectiveConfigCollector(
    IHttpClientFactory httpClientFactory,
    IOptions<IntrospectionOptions> options,
    ILogger<HttpEffectiveConfigCollector> logger) : IEffectiveConfigCollector
{
    public const string HttpClientName = "Introspection";

    private readonly IntrospectionOptions _options = options.Value;

    public async Task<EffectiveCollection> CollectAsync(CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));

        // FR-15: 対象サービスの自己申告を並列に収集する（応答時間が対象サービス数に比例して
        // 増えないよう Task.WhenAll でまとめる。集約は完了後に単一スレッドで行い競合を避ける）。
        var results = await Task.WhenAll(
            _options.Services.Select(kv => CollectOneAsync(client, kv.Key, kv.Value, ct)));

        var services = new List<ServiceIntrospectionDto>();
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var unreachable = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (service, report) in results)
        {
            if (report is null)
            {
                unreachable.Add(service);
                continue;
            }
            services.Add(report);
            reachable.Add(service);
        }

        return new EffectiveCollection(services, reachable, unreachable);
    }

    // FR-15: 1 サービス分の自己申告を収集する。到達不能・空応答は report=null で返し、呼び出し側で
    // UnreachableServices に集約する（適用漏れと到達不能を区別するため。IADR-0029）。
    private async Task<(string Service, ServiceIntrospectionDto? Report)> CollectOneAsync(
        HttpClient client, string service, string baseUrl, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + _options.Path;
        try
        {
            var report = await client.GetFromJsonAsync<ServiceIntrospectionDto>(url, ct);
            if (report is null)
                logger.LogWarning(
                    "Introspection for {Service} at {Url} returned empty body", service, url);
            return (service, report);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to collect introspection for {Service} at {Url}", service, url);
            return (service, null);
        }
    }
}
