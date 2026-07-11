using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Bff.Composition;
using Platform.Bff.Foundation.Endpoints;

namespace Platform.Bff.Tests;

// FR-14, IADR-0063, Issue #229: BFF エンドポイント合成点（器）の回帰テスト。
// 合成点経由（MapComposedBffEndpoints）が、従来の個別 9 呼び出しと同数のルートグループ（EndpointDataSource）を
// 登録することを固定する（器の導入が非破壊であることの保証。実ルートの動作は既存の統合テスト群が担保する）。
// 注: 個々のエンドポイント実体（RouteEndpoint）の materialize は RequestDelegateFactory による DI 解決を伴うため、
// ここでは DataSource（MapGroup 単位）の数で等価性を確認する。
public class BffEndpointCompositionTests
{
    private static int CollectDataSourceCount(Action<IEndpointRouteBuilder> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        var app = builder.Build();
        map(app);
        return ((IEndpointRouteBuilder)app).DataSources.Count;
    }

    [Fact]
    public void Composition_registers_the_same_number_of_route_groups_as_individual_map_calls()
    {
        var viaComposition = CollectDataSourceCount(app => app.MapComposedBffEndpoints());

        var viaIndividual = CollectDataSourceCount(app =>
        {
            app.MapSearchBffEndpoints();
            app.MapDocumentBffEndpoints();
            app.MapAnalysisBffEndpoints();
            app.MapFeedbackBffEndpoints();
            app.MapDashboardBffEndpoints();
            app.MapConfigBffEndpoints();
            app.MapConversionBffEndpoints();
            app.MapAuthzBffEndpoints();
            app.MapDataSourceBffEndpoints();
        });

        viaComposition.Should().BeGreaterThan(0);
        viaComposition.Should().Be(viaIndividual);
    }

    [Fact]
    public void Composition_registry_holds_all_endpoint_modules()
    {
        // 現時点は platform 同居の 9 モジュール。ユニット移設・追加は合成点の登録簿で行う（IADR-0063）。
        BffEndpointComposition.Modules.Should().HaveCount(9);
    }
}
