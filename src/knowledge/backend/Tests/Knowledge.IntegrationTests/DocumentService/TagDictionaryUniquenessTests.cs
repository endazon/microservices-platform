using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using System.Net;
using System.Net.Http.Json;

namespace Knowledge.IntegrationTests.DocumentService;

// FR-09, SC-09, #634: タグ辞書の名前一意性（実 PostgreSQL）。
//
// **単体テストでは踏めない経路である。** `DocumentService.Tests` は EF InMemory を使っており、
// **InMemory プロバイダは一意インデックスを強制しない**（実測）。したがって
// 「事前検証をすり抜けた同時登録が DB の一意制約で弾かれる」経路は**実 DB でしか確かめられない**。
//
// 契約は「重複は 409」であり、**素の 500 にしてはならない**
// （`AuthzEndpoints` の `AttributeDefinition` が同型の先例である）。
[Trait("Category", "Integration")]
public sealed class TagDictionaryUniquenessTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private DocumentServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        if (!postgres.IsAvailable || !rabbit.IsAvailable) return;
        _factory = new DocumentServiceFactory(postgres, rabbit);
        _client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<global::DocumentService.Infrastructure.Persistence.DocumentDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    // SC-09: 「新しい名前は既存値と重複しない」。**逐次の重複は事前検証が 409 を返す。**
    [Fact]
    public async Task CreateTag_Duplicate_Returns409()
    {
        DockerRequired.SkipUnlessAvailable();
        var name = $"重複-{Guid.NewGuid():N}";

        (await _client.PostAsJsonAsync("/tags", new { name }, TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Created);
        (await _client.PostAsJsonAsync("/tags", new { name }, TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
    }

    // **同時登録（race）でも 500 にならない。**
    // 事前検証（`AnyAsync`）と保存の間に別の要求が同名を入れられるため、事前検証だけでは埋まらない。
    // DB の一意インデックス違反を `DbUpdateException` として捕まえ、409 へ写している。
    [Fact]
    public async Task CreateTag_Concurrently_ExactlyOneSucceeds_AndNoServerError()
    {
        DockerRequired.SkipUnlessAvailable();
        var name = $"競合-{Guid.NewGuid():N}";

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => _client.PostAsJsonAsync("/tags", new { name })));

        try
        {
            responses.Should().OnlyContain(
                r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict,
                "重複は 409 が契約であり、素の 500 にしてはならない");
            responses.Count(r => r.StatusCode == HttpStatusCode.Created)
                .Should().Be(1, "一意インデックスが 1 件だけを通す");

            // 辞書にも 1 件しか入っていない。
            var list = await (await _client.GetAsync("/tags", TestContext.Current.CancellationToken))
                .Content.ReadFromJsonAsync<TagListResponse>(TestContext.Current.CancellationToken);
            list!.Tags.Count(t => t.Name == name).Should().Be(1);
        }
        finally
        {
            foreach (var r in responses) r.Dispose();
        }
    }

    private sealed record TagListResponse(List<TagEntry> Tags);
    private sealed record TagEntry(Guid Id, string Name, int UsageCount);
}
