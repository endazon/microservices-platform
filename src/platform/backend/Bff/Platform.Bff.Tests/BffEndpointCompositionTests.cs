using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using AiStockTrading.Bff.Endpoints;
using Knowledge.Bff.Endpoints;
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
            app.MapAssumptionsBffEndpoints();
            app.MapRiskControlsBffEndpoints();
            app.MapMonitorBffEndpoints();
        });

        viaComposition.Should().BeGreaterThan(0);
        viaComposition.Should().Be(viaIndividual);
    }

    [Fact]
    public void Composition_registry_holds_all_endpoint_modules()
    {
        // 全 12 モジュール。ナレッジ 7 ドメイン（Search/Document/Analysis/Feedback/Dashboard/Conversion/DataSource）は
        // knowledge の Knowledge.Bff.Endpoints へ移設済み・例外3 で合成点参照。platform 固有 2（Config/Authz）は
        // platform 同居。AST の Assumptions（#283・SC-01）／RiskControls（#287・AST/SC-02/AST/SC-03）／Monitor（#288・AST/SC-02 watchlist）は
        // #286（IADR-0073）で AiStockTrading.Bff.Endpoints（AST submodule の unit-owned Bff）へ移設済み・例外3 で合成点参照。
        BffEndpointComposition.Modules.Should().HaveCount(12);
    }

    // 内容一致の検証（claude-review 指摘対応）: 合成点経由でビルドした実アプリ（全 DI 込み）の実体化ルートが、
    // 期待する /bff/* ルートグループ集合と過不足なく一致することを固定する。件数だけでなく各グループの存在・
    // 不在（モジュールのドロップ/入替/重複）を検出する。
    [Fact]
    public void Composition_maps_exactly_the_expected_bff_route_groups()
    {
        // 期待する 12 ルートグループのプレフィックス（各 BFF エンドポイントモジュールの MapGroup）。
        string[] expectedGroups =
        [
            "/bff/admin/authz",
            "/bff/admin/config",
            "/bff/analysis",
            "/bff/assumptions",
            "/bff/conversion/jobs",
            "/bff/dashboard",
            "/bff/datasources",
            "/bff/documents",
            "/bff/feedback",
            "/bff/monitor",
            "/bff/risk-controls",
            "/bff/search",
        ];

        using var factory = new BffTestFactory();
        _ = factory.Services; // 合成点経由（Program.cs の MapComposedBffEndpoints）でホストをビルドさせる。
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        var routePatterns = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => "/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/'))
            .ToList();

        // /bff/ 配下の各ルートを、どの期待グループにも属さないもの＝想定外として検出する。
        var bffRoutes = routePatterns.Where(p => p.StartsWith("/bff/", StringComparison.Ordinal)).ToList();
        bffRoutes.Should().NotBeEmpty();

        // 各期待グループに 1 つ以上のルートが存在する（モジュールのドロップ/入替を検出）。
        foreach (var group in expectedGroups)
        {
            bffRoutes.Should().Contain(
                p => p == group || p.StartsWith(group + "/", StringComparison.Ordinal),
                $"ルートグループ {group} が合成点経由で登録されているべき");
        }

        // 期待グループ以外の /bff/* トップレベルグループが存在しない（想定外モジュールの混入を検出）。
        bffRoutes.Should().OnlyContain(
            p => expectedGroups.Any(g => p == g || p.StartsWith(g + "/", StringComparison.Ordinal)),
            "期待外の /bff/* ルートグループが登録されていないべき");
    }

    // #286, IADR-0073: AST 3 モジュール（Assumptions/RiskControls/Monitor）が interim の platform 同居から
    // AST の unit-owned Bff（AiStockTrading.Bff.Endpoints・例外3）へ移設されたことを固定する（所在移行の回帰防止）。
    // 拡張メソッドを提供する静的クラスの所属アセンブリ・名前空間が AST unit-owned Bff であることを検証する
    // （platform 同居へ戻す退行を検出）。ルートの振る舞いは既存の Bff*EndpointTests と本クラスの合成テストが担保する。
    [Theory]
    [InlineData(typeof(AssumptionsBffEndpoints))]
    [InlineData(typeof(RiskControlsBffEndpoints))]
    [InlineData(typeof(MonitorBffEndpoints))]
    public void Ast_bff_modules_live_in_the_ast_unit_owned_assembly(Type moduleType)
    {
        moduleType.Assembly.GetName().Name.Should().Be(
            "AiStockTrading.Bff.Endpoints",
            "AST の BFF モジュールは例外3 の unit-owned Bff（AST submodule）に所属すべき（#286・IADR-0073）");
        moduleType.Namespace.Should().Be("AiStockTrading.Bff.Endpoints");
    }
}
