using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Bff.Tests;

// FR-01/FR-02, UC-04, SC-06, IADR-0089 (#342): /bff/datasources の上流（named client "DataSourceService"）の
// 解決を回帰固定する。live で BFF が datasource-service:5002（コード既定）を叩き 21 秒タイムアウト→502 になった
// のは、実効ポート :8080 への上書き（Services:DataSourceService 設定 / Helm bff.extraEnv・compose env）が欠落
// していたためである。ここでは「named client の BaseAddress が Services 設定で解決される（コード既定の直書きに
// 退行していない）」ことを固定する。manifest 側の実効ポート（:8080）解決は scripts/check-bff-downstreams.js が担保する。
public class BffDownstreamResolutionTests
{
    [Fact]
    public void DataSourceService_client_base_address_is_resolved_from_Services_configuration()
    {
        using var factory = new BffTestFactory();
        var httpFactory = factory.Services.GetRequiredService<IHttpClientFactory>();

        // BffTestFactory は Services:DataSourceService = http://datasource-service:8080 を注入している。
        var client = httpFactory.CreateClient("DataSourceService");

        client.BaseAddress.Should().Be(new Uri("http://datasource-service:8080"),
            "DataSourceService の上流は Services:DataSourceService 設定で解決されるべき" +
            "（コード既定 :5002 の直書きは live 502 の原因＝#342）");
    }

    // 注: 上流解決 → 実プロキシの結線（/bff/datasources が解決上流へ中継し後段データソースを返す）の
    // エンドツーエンドは既存の BffDataSourceEndpointTests.GetList_AsAdmin_ReturnsDataSources が担保するため、
    // ここでは重複させず BaseAddress 解決（上記）のみを固定する。
}
