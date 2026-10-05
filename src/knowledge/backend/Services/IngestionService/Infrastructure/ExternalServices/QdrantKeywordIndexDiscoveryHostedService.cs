using IngestionService.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IngestionService.Infrastructure.ExternalServices;

// FR-04, FR-05, SC-01, SC-08, [[IADR-0502]] 決定 3 (#1760): **既存の点に現れる属性キーへ、キーワード索引を張る。**
//
// 属性キー（`attributes.<key>`）は動的であり、書き込みの口が張るのは「このプロセスが書いたキー」だけである。
// 稼働中の配備には、再起動後に一度も書かれていないキーを持つ点が残る。起動後のバックグラウンドで全コレクションを
// 走査して拾い、同じ経路（冪等な `CreatePayloadIndex`）で張る。
//
// **起動を塞がない**（`BackgroundService`）。`QdrantCjkNgramBackfillHostedService` と同じ作法で、
// 🔴 **例外はここで必ず捕まえる**（未捕捉例外は既定でホストを止める）。索引の欠落で取り込みを落とさない。
public sealed class QdrantKeywordIndexDiscoveryHostedService(
    IServiceProvider services,
    ILogger<QdrantKeywordIndexDiscoveryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIngestionVectorStore>();
        try
        {
            var created = await store.EnsureKeywordIndexesForExistingPointsAsync(stoppingToken);
            logger.LogInformation(
                "Ensured {Count} keyword payload index(es) for attribute keys found on existing points; "
                + "scoped attribute values (facet) cover pre-existing keys from now on", created);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停止要求。次の起動で最初から走査する（張り済みのキーは冪等）。
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to ensure keyword payload indexes for attribute keys on existing points; "
                + "scoped attribute values (facet) return no candidates for keys not written since this start");
        }
    }
}
