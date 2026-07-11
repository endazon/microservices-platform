using FluentAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace Knowledge.IntegrationTests.AuthorizationService;

// FR-05, UC-05, ADR-0004: ABAC スコープ解決 統合テスト（実 PostgreSQL）
[Trait("Category", "Integration")]
public sealed class AbacScopeTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private AuthorizationServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.IsAvailable) return;
        _factory = new AuthorizationServiceFactory(postgres);
        _client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<global::AuthorizationService.Api.Foundation.Persistence.AuthorizationDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [DockerFact]
    public async Task ResolveScope_NoMatchingPolicies_ReturnsEmptyFilters()
    {
        // Use a department that is never covered by any seeded policy,
        // so the result is empty regardless of test execution order.
        var req = new AccessScopeRequest("user-001", new Dictionary<string, string>
        {
            ["department"] = "no_matching_dept_xyz"
        });

        var resp = await _client.PostAsJsonAsync("/authz/scope", req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await resp.Content.ReadFromJsonAsync<AccessScopeResponse>();
        result.Should().NotBeNull();
        result!.UserId.Should().Be("user-001");
        result.AllowedFilters.Should().BeEmpty();
    }

    [DockerFact]
    public async Task CreatePolicy_ThenResolveScope_ReturnsFilters()
    {
        var policy = new
        {
            name = "engineering-read",
            action = "read",
            userConditions = new { department = new[] { "engineering" } },
            documentConditions = new { department = new[] { "engineering" } },
            isActive = true
        };
        var createResp = await _client.PostAsJsonAsync("/authz/policies", policy);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var req = new AccessScopeRequest("user-002", new Dictionary<string, string>
        {
            ["department"] = "engineering"
        });
        var scopeResp = await _client.PostAsJsonAsync("/authz/scope", req);
        scopeResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await scopeResp.Content.ReadFromJsonAsync<AccessScopeResponse>();
        result!.AllowedFilters.Should().NotBeEmpty();
        result.AllowedFilters.Should().Contain(f => f.Key == "department");
    }

    [DockerFact]
    public async Task ListPolicies_ReturnsOk()
    {
        var resp = await _client.GetAsync("/authz/policies");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
