using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;
using AuthorizationService.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Knowledge.IntegrationTests.AuthorizationService;

// FR-05, UC-05, ADR-0004: ABAC スコープ解決 統合テスト（実 PostgreSQL）
[Trait("Category", "Integration")]
public sealed class AbacScopeTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private AuthorizationServiceFactory _factory = null!;
    private HttpClient _client = null!;

    // FR-05, NFR-09, 計画 ADR-0088 決定 2, [[IADR-0413]] 決定 3 (#1333):
    // 🔴 **スコープ解決は `ServiceCaller` を要る。管理系（`AdminOnly`）とは面が違う。**
    // 同じ主体で両方を叩くと、`ADR-0088` 決定 2 が要求した統制がテストから見えなくなる。
    private HttpClient ServiceCaller()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            IntegrationTestAuthHandler.RolesHeader, PlatformAuthPolicies.ServiceRole);
        return client;
    }

    // FR-05, 計画 ADR-0088 決定 1, [[IADR-0413]] 決定 1 (#1333):
    // 🔴 **ABAC 判定に使う属性は要求本文ではなく身元プロバイダから来る。**
    // したがって「この利用者はこういう属性を持つ」はここで作る。
    // 器の IdP は偽物（`in-memory`）で、利用者は種として入っている ——
    // その属性を差し替えることで、**試験ごとに独立した属性**を用意できる
    // （共有 DB のポリシーが他試験の分も混ざるため、値は毎回一意にする）。
    private async Task GiveUserAsync(string userId, string key, string value)
    {
        var identity = _factory.Services.GetRequiredService<IIdentityAdminClient>();
        var updated = await identity.ReplaceAttributesAsync(
            userId, new Dictionary<string, string> { [key] = value }, TestContext.Current.CancellationToken);
        updated.Should().NotBeNull($"種の利用者 {userId} が偽 IdP に居ること");
    }

    public async ValueTask InitializeAsync()
    {
        if (!postgres.IsAvailable) return;
        _factory = new AuthorizationServiceFactory(postgres);
        _client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<global::AuthorizationService.Infrastructure.Persistence.AuthorizationDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task ResolveScope_NoMatchingPolicies_ReturnsEmptyFilters()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        // どのポリシーにも当たらない部署を**身元プロバイダ側へ**置く（実行順に依存しない）。
        // 🔴 本文へ書いても評価には使われない（`ADR-0088` 決定 1）。
        await GiveUserAsync("u-tanaka", "department", $"no_matching_dept_{Guid.NewGuid():N}");

        // 🔴 本文の属性は**わざと嘘を入れてある**。使われていないことがこれで見える ——
        // 使われていたら下の `AllowedFilters` が空にならない可能性がある。
        var req = new AccessScopeRequest("tanaka.taro", new Dictionary<string, string>
        {
            ["department"] = "engineering"
        });

        var resp = await ServiceCaller().PostAsJsonAsync("/authz/scope", req, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await resp.Content.ReadFromJsonAsync<AccessScopeResponse>(TestContext.Current.CancellationToken);
        result.Should().NotBeNull();
        result!.UserId.Should().Be("tanaka.taro");
        result.AllowedFilters.Should().BeEmpty();
    }

    [Fact]
    public async Task CreatePolicy_ThenResolveScope_ReturnsFilters()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        var policy = new
        {
            name = "engineering-read",
            action = "read",
            userConditions = new { department = new[] { "engineering" } },
            documentConditions = new { department = new[] { "engineering" } },
            isActive = true
        };
        var createResp = await _client.PostAsJsonAsync("/authz/policies", policy, TestContext.Current.CancellationToken);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);

        // 🔴 属性は身元プロバイダ側に置く（`ADR-0088` 決定 1）。本文は空でよい ——
        // **空でも通ること**が「主張ではなく IdP が根拠である」ことの現れである。
        await GiveUserAsync("u-sato", "department", "engineering");

        var req = new AccessScopeRequest("sato.hanako", []);
        var scopeResp = await ServiceCaller().PostAsJsonAsync("/authz/scope", req, TestContext.Current.CancellationToken);
        scopeResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await scopeResp.Content.ReadFromJsonAsync<AccessScopeResponse>(TestContext.Current.CancellationToken);
        result!.AllowedFilters.Should().NotBeEmpty();
        result.AllowedFilters.Should().Contain(f => f.Key == "department");
    }

    [Fact]
    public async Task ListPolicies_ReturnsOk()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        var resp = await _client.GetAsync("/authz/policies", TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
