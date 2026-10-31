using AwesomeAssertions;
using DataSourceService.Domain;
using DataSourceService.Features.DataSources.Introspection;
using DataSourceService.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Introspection;

namespace DataSourceService.Tests.Features.DataSources;

// FR-15, FR-01, SC-06, SC-11, IADR-0666 (#2285 の (b)(ii)): 計画 10_composability §6 の「コネクタ一覧」（SC-06 に登録したデータソースと
// 有効・無効。planning#850 の裁定 (ii)）を、データソースサービスが要求のたびの寄与で構成取得 API へ申告する。
[Trait("TestKind", "Integration")]
public sealed class RegisteredDataSourcesIntrospectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<DataSource> AddAsync(IServiceProvider services, string name, string type)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
        var ds = DataSource.Create(name, type, "https://source.example.test", new() { ["apiToken"] = "secret-marker-7f3a" });
        db.DataSources.Add(ds);
        await db.SaveChangesAsync(Ct);
        return ds;
    }

    private static async Task DisableAsync(IServiceProvider services, Guid id)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
        (await db.DataSources.FindAsync([id], Ct))!.Disable();
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<ServiceIntrospectionDto> CurrentAsync(IServiceProvider services) =>
        await IntrospectionContributions.ApplyAsync(
            services.GetRequiredService<ServiceIntrospectionDto>(), services.GetServices<IIntrospectionContributor>(), Ct);

    [Fact]
    public void 本番の配線が登録済みのデータソースの寄与を登録している()
    {
        using var factory = new TestWebApplicationFactory();
        factory.Services.GetServices<IIntrospectionContributor>()
            .Should().ContainSingle(c => c is RegisteredDataSourcesIntrospectionContributor);
    }

    // 受け入れ基準: 登録・無効化は次の要求に反映される。状態に依らず全行を、名前の順に載せる。コネクタの種別（静的な申告）は変わらない。
    [Fact]
    public async Task 登録と無効化は次の申告に反映され_種別の申告は変わらない()
    {
        using var factory = new TestWebApplicationFactory();
        var services = factory.Services;
        var before = await CurrentAsync(services);
        before.DataSources.Should().BeEmpty("まだ登録していない");

        var wiki = await AddAsync(services, "社内 Wiki", "wiki");
        var share = await AddAsync(services, "共有フォルダ", "filesystem");
        var afterAdd = await CurrentAsync(services);
        afterAdd.DataSources.Should().Equal(
            new DataSourceSummaryDto(share.Id.ToString("D"), "共有フォルダ", "filesystem", true),
            new DataSourceSummaryDto(wiki.Id.ToString("D"), "社内 Wiki", "wiki", true));

        await DisableAsync(services, wiki.Id);
        var afterDisable = await CurrentAsync(services);
        afterDisable.DataSources.Single(d => d.Id == wiki.Id.ToString("D")).Enabled.Should().BeFalse("無効化したデータソースも載り、無効と出る");

        afterDisable.Connectors.Should().BeEquivalentTo(before.Connectors, "コネクタの種別（プラグイン 4 種）は寄与で変わらない");
    }

    // 🔴 接続先・設定・資格情報を載せない。DTO の形（4 つのメンバー）で固定し、値の側も確かめる。
    [Fact]
    public async Task 申告には接続先も設定も資格情報も載らない()
    {
        typeof(DataSourceSummaryDto).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(["Id", "Name", "Type", "Enabled"]);

        using var factory = new TestWebApplicationFactory();
        await AddAsync(factory.Services, "社内 Wiki", "wiki");
        var report = await CurrentAsync(factory.Services);

        var text = System.Text.Json.JsonSerializer.Serialize(report.DataSources);
        text.Should().NotContain("secret-marker-7f3a").And.NotContain("source.example.test");
    }
}
