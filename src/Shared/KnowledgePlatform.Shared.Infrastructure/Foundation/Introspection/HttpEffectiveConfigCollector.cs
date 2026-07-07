using System.Net.Http.Json;
using KnowledgePlatform.Shared.Contracts.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;

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
        var services = new List<ServiceIntrospectionDto>();
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var unreachable = new HashSet<string>(StringComparer.Ordinal);

        var client = httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));

        foreach (var (service, baseUrl) in _options.Services)
        {
            var url = baseUrl.TrimEnd('/') + _options.Path;
            try
            {
                var report = await client.GetFromJsonAsync<ServiceIntrospectionDto>(url, ct);
                if (report is null)
                {
                    unreachable.Add(service);
                    logger.LogWarning(
                        "Introspection for {Service} at {Url} returned empty body", service, url);
                    continue;
                }
                services.Add(report);
                reachable.Add(service);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unreachable.Add(service);
                logger.LogWarning(ex,
                    "Failed to collect introspection for {Service} at {Url}", service, url);
            }
        }

        return new EffectiveCollection(services, reachable, unreachable);
    }
}
