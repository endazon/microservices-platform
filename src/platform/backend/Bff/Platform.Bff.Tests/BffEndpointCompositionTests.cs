using AwesomeAssertions;
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
            app.MapTagDictionaryBffEndpoints();
            app.MapGraphBffEndpoints();
            app.MapEdgeTypeDictionaryBffEndpoints();
            // #451, FR-19, FR-20, SC-19, SC-20: 個人資料・Obsidian 連携設定。
            app.MapPrivateNoteBffEndpoints();
            // #1199, FR-13, UC-07, SC-04: Wiki 前段の 4 経路（後段は WikiService）。
            app.MapWikiBffEndpoints();
            app.MapAssumptionsBffEndpoints();
            app.MapRiskControlsBffEndpoints();
            app.MapMonitorBffEndpoints();
            // #452, FR-16, UC-09, SC-12: MCP クライアント登録管理（後段は McpServer）。
            app.MapMcpClientBffEndpoints();
            // #452, FR-05, FR-09, UC-05, SC-17: 利用者アカウント管理（後段は AuthorizationService）。
            app.MapUserAdminBffEndpoints();
            // NFR, SC-16, ADR-0032 / IADR-0251 / #439 第 3 段(3a): BFF セッションの入口。
            app.MapAuthBffEndpoints();
            // #600, FR-22, UC-11: 利用者本人へのアプリ内通知（後段は NotificationService）。
            app.MapNotificationBffEndpoints();
        });

        viaComposition.Should().BeGreaterThan(0);
        viaComposition.Should().Be(viaIndividual);
    }

    [Fact]
    public void Composition_registry_holds_all_endpoint_modules()
    {
        // 全 18 モジュール。ナレッジ 10 ドメイン（Search/Document/Analysis/Feedback/Dashboard/Conversion/DataSource/TagDictionary/Graph/PrivateNote）は
        // knowledge の Knowledge.Bff.Endpoints へ移設済み・例外3 で合成点参照。platform 固有 2（Config/Authz）は
        // platform 同居（#452 の McpClient・UserAdmin を含めて 4 つ）。AST の Assumptions（#283・AST/SC-01）／RiskControls（#287・AST/SC-02/AST/SC-03）／Monitor（#288・AST/SC-02 watchlist）は
        // #286（IADR-0073）で AiStockTrading.Bff.Endpoints（AST submodule の unit-owned Bff）へ移設済み・例外3 で合成点参照。
        // NFR, SC-16, ADR-0032 / IADR-0251 / #439 第 3 段(3a): BFF セッションの入口（Auth）を追加した。
        // #451, FR-19, FR-20, SC-19, SC-20: 個人資料・Obsidian 連携設定（PrivateNote）を追加した。
        // #452, FR-16, UC-09, SC-12: MCP クライアント登録管理（McpClient）を追加した（platform 同居。
        // 後段の McpServer が platform ユニットのサービスであるため）。
        // #452, FR-05, FR-09, UC-05, SC-17: 利用者アカウント管理（UserAdmin）を追加した（platform 同居。
        // 後段の AuthorizationService が platform ユニットのサービスであるため。IADR-0301 決定 1）。
        // #600, FR-22, UC-11: 利用者本人へのアプリ内通知（Notification）を追加した（platform 同居。
        // 後段の NotificationService が platform ユニットのサービスであるため。IADR-0346 決定 1）。
        // #1199, FR-13, UC-07, SC-04: Wiki 前段の 4 経路（Wiki）を追加した（Knowledge.Bff.Endpoints。
        // 後段の WikiService が knowledge ユニットのサービスであるため。IADR-0355 決定 1）。
        BffEndpointComposition.Modules.Should().HaveCount(21);
    }

    // 内容一致の検証（claude-review 指摘対応）: 合成点経由でビルドした実アプリ（全 DI 込み）の実体化ルートが、
    // 期待する /bff/* ルートグループ集合と過不足なく一致することを固定する。件数だけでなく各グループの存在・
    // 不在（モジュールのドロップ/入替/重複）を検出する。
    [Fact]
    public void Composition_maps_exactly_the_expected_bff_route_groups()
    {
        // 期待する 20 ルートグループのプレフィックス（各 BFF エンドポイントモジュールの MapGroup）。
        string[] expectedGroups =
        [
            // #451, FR-19, FR-20, SC-19, SC-20: 個人資料と同期端末（後段は DocumentService の
            // /private-notes*）。**端末の群（/bff/private-notes/devices）もこの接頭辞に含まれる。**
            "/bff/private-notes",
            "/bff/admin/authz",
            "/bff/admin/config",
            // #452, FR-16, UC-09, SC-12: MCP クライアント登録管理（AdminOnly の透過中継）。
            // **`/bff/admin/mcp-clients/tools` もこの接頭辞に含まれる**（公開ツール一覧は読み取りだけ）。
            "/bff/admin/mcp-clients",
            // #452, FR-05, FR-09, UC-05, SC-17: 利用者アカウント管理（AdminOnly の透過中継）。
            // **`/bff/admin/users/assignable-roles` もこの接頭辞に含まれる**（割当可能ロールの値域）。
            // 🔴 **`POST /bff/admin/users`（新規作成）は無い** —— 計画が本画面からの作成を禁じている。
            "/bff/admin/users",
            "/bff/analysis",
            "/bff/assumptions",
            // NFR, SC-16, ADR-0032 / IADR-0251 / #439 第 3 段(3a): BFF セッションの入口。
            // ログイン開始・ログアウト・現在の身元。**トークンはここからも出さない。**
            // OIDC のコールバック（/bff/auth/callback）はハンドラが直接受けるため、
            // 端点として登録されず本一覧にも現れない。
            "/bff/auth",
            // FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会（ADR-0043）。
            "/bff/attribute-values",
            "/bff/conversion/jobs",
            "/bff/dashboard",
            "/bff/datasources",
            // #1241, FR-17, SC-09, ADR-0033: 辺の型辞書の管理（追加・改名・削除）。後段は GraphService。
            // 🔴 **`/bff/graph/edge-types`（描画用カタログ・認証のみ）とは別の接頭辞である。**
            // 同じ口にすると、一般利用者が 403 になるか ABAC 未適用の集計値が漏れるかのどちらかになる。
            "/bff/edge-types",
            "/bff/documents",
            "/bff/feedback",
            "/bff/monitor",
            "/bff/risk-controls",
            "/bff/search",
            // FR-09, SC-09, #640: タグ辞書の管理（追加・改名・削除）。後段は DocumentService
            // （knowledge ユニット）なので Knowledge.Bff.Endpoints が配る。
            "/bff/tags",
            // #916a, FR-17, UC-10: グラフ読み取り（後段は GraphService。Authorization を伝播する）。
            "/bff/graph",
            // #600, FR-22, UC-11: 本人宛のアプリ内通知（後段は NotificationService。**認証必須・
            // ロールは問わない**。絞るのは役割ではなく主体＝JWT の sub）。
            "/bff/notifications",
            // #1199, FR-13, UC-07, SC-04, ADR-0073 決定 2・4: Wiki 前段の 4 経路（後段は WikiService。
            // **認証必須・ロールは問わない**。可視性を決めるのは役割ではなく ABAC である）。
            // **`/bff/wiki/pages/by-doc/{documentId}` もこの接頭辞に含まれる。**
            "/bff/wiki",
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
