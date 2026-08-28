using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Knowledge.IntegrationTests.AuthorizationService;

// FR-05, FR-09, SC-17, IADR-0301 決定 3 (#1044): **統合テスト器が AuthorizationService の
// ホストを起こせること**を、コンテナ無しで固定する。
//
// 🔴 **なぜ `AbacScopeTests` では捕まらなかったのか。**
// 同クラスの `InitializeAsync` は `if (!postgres.IsAvailable) return;` で早期 return するため、
// Docker の無い環境では**ホストを一度も起こさない**。起動時例外は Docker のある
// `develop` の Integration でしか現れず、**PR の `ci`（`--filter "Category!=Integration"`）では
// 構造的に検出できなかった**（IADR-0232 決定 1 の設計どおりの回収）。
//
// 本クラスは `Category=Integration` を**付けない**。DbContext を InMemory へ差し替えることで
// `Program.cs` の起動時 `MigrateAsync` を迂回し（`IsRelational()` が false になる）、
// **実 DB もブローカも無しでホスト構築だけを通す**。これで PR の `ci` が同じ退行を
// マージ前に赤くする。
//
// **測っているのは「起動時に必須の構成を器が与えているか」だけ**である。ABAC の判定・
// 永続化・実 IdP への反映はここでは測らない（それぞれ AbacScopeTests / 単体テストの担当）。
[Trait("Category", "EndpointRouting")]
public sealed class AuthorizationHostBootTests
{
    // PostgresFixture は `InitializeAsync` を呼ばない限りコンテナを起動しない
    // （`ConnectionString` は null のまま＝器の既定 "Host=localhost" へ倒れる）。
    // DbContext は下で InMemory へ差し替えるので、この値へは一度も接続しない。
    private static WebApplicationFactory<global::AuthorizationService.AuthorizationServiceTestMarker> Boot()
        => new AuthorizationServiceFactory(new PostgresFixture())
            .WithWebHostBuilder(b => b.ConfigureServices(ReplaceWithInMemoryDb));

    private static void ReplaceWithInMemoryDb(IServiceCollection services)
    {
        var toRemove = services
            .Where(d => d.ServiceType
                     == typeof(DbContextOptions<global::AuthorizationService.Infrastructure.Persistence.AuthorizationDbContext>)
                     || (d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericTypeDefinition().FullName?
                                .Contains("IDbContextOptionsConfiguration") == true
                         && d.ServiceType.GenericTypeArguments.Length == 1
                         && d.ServiceType.GenericTypeArguments[0]
                            == typeof(global::AuthorizationService.Infrastructure.Persistence.AuthorizationDbContext)))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<global::AuthorizationService.Infrastructure.Persistence.AuthorizationDbContext>(
            opt => opt.UseInMemoryDatabase($"AuthzBoot_{Guid.NewGuid()}"));
    }

    [Fact]
    public void 器が起こしたホストは身元プロバイダを解決できる()
    {
        using var factory = Boot();

        // ホスト構築そのものが検査対象である（`IdentityAdmin:Provider` が無ければここで
        // InvalidOperationException になる）。解決先まで見るのは、**宣言が
        // `in-memory` として効いていること**を確かめるためである。
        var client = factory.Services
            .GetRequiredService<global::AuthorizationService.Domain.Ports.IIdentityAdminClient>();

        client.Should().BeOfType<
            global::AuthorizationService.Infrastructure.ExternalServices.InMemoryIdentityAdminClient>(
            "統合テストは実 IdP を持たない。**偽物であることを明示的に宣言する**"
            + "（IdentityAdmin:Provider は既定を持たないため、宣言が無ければホストが起動しない）");
    }

    [Fact]
    public void 器は身元プロバイダを実IdPではなく偽物として宣言する()
    {
        // 値そのものを固定する。`keycloak` を選ぶと実 Keycloak の資格情報が要り、
        // **統合テストが実 IdP へ書き込みに行く**（IADR-0301 決定 3）。
        AuthorizationServiceFactory.IdentityAdminProviderValue.Should().Be("in-memory");
        AuthorizationServiceFactory.IdentityAdminProviderKey.Should().Be("IdentityAdmin:Provider");
    }
}
