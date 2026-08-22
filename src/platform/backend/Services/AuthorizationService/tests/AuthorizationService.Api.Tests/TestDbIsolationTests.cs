using AwesomeAssertions;
using System.Net.Http.Json;

namespace AuthorizationService.Api.Tests;

// NFR, #660: **テストクラスごとに InMemory DB が分離されていること**を決定的に固定する。
//
// 従前 `TestWebApplicationFactory` は DB 名を固定文字列（`"AuthzTest"`）で張っており、
// **`IClassFixture` がクラスごとに別インスタンスを作るのに、ストアだけがプロセス内で共有されていた**。
// xUnit は既定でテストクラスを並列に走らせるため、`PolicyDryRunValidationTests` の
// `Validate_DoesNotPersistAnything`（`before` / `after` の 2 回ポリシー数を読む）が、
// **その間に別クラスが投入したポリシーを数えて確率的に落ちていた**。
//
// ★ **「落ちるまで回す」形の再現テストは書かない。** 競合は確率的で、CI で不安定になるだけである。
//    代わりに**同じ形の分離そのもの**を主張する —— ファクトリを 2 つ作り、
//    一方への書き込みが他方から見えないことを見る。これは決定的で、
//    DB 名を固定へ戻すと**必ず**落ちる（作業仕様書 §変異試験）。
public class TestDbIsolationTests
{
    private static object PolicyBody(string name) => new
    {
        Name = name,
        Action = "read",
        UserConditions = new Dictionary<string, string[]>(),
        DocumentConditions = new Dictionary<string, string[]>(),
    };

    private sealed record PolicyDto(Guid Id, string Name);

    // #660: 別インスタンスのファクトリは**別の DB** を持つ。
    [Fact]
    public async Task SeparateFactoryInstances_DoNotShareTheirStore()
    {
        using var first = new TestWebApplicationFactory();
        using var second = new TestWebApplicationFactory();

        var name = $"isolation-probe-{Guid.NewGuid()}";
        var created = await first.CreateClient().PostAsJsonAsync("/authz/policies", PolicyBody(name), TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();

        // 書いた側からは見える。
        var fromFirst = await first.CreateClient().GetFromJsonAsync<List<PolicyDto>>("/authz/policies", TestContext.Current.CancellationToken);
        fromFirst.Should().Contain(p => p.Name == name);

        // ★ もう一方からは見えない。**ここが #660 の核心である。**
        var fromSecond = await second.CreateClient().GetFromJsonAsync<List<PolicyDto>>("/authz/policies", TestContext.Current.CancellationToken);
        fromSecond.Should().NotContain(p => p.Name == name,
            "テストクラスごとに DB が分離されていないと、他クラスの副作用を数えてしまう（#660）");
    }

    // #660: 同一インスタンス内では従来どおり共有される（**分離しすぎていないことの側**）。
    // `IClassFixture` を使うテストは 1 クラス内で状態を引き継ぐので、ここを壊してはならない。
    [Fact]
    public async Task SameFactoryInstance_SharesItsStore()
    {
        using var factory = new TestWebApplicationFactory();

        var name = $"same-instance-{Guid.NewGuid()}";
        (await factory.CreateClient().PostAsJsonAsync("/authz/policies", PolicyBody(name), TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();

        // **別の HttpClient** から読んでも同じストアを見る。
        var policies = await factory.CreateClient().GetFromJsonAsync<List<PolicyDto>>("/authz/policies", TestContext.Current.CancellationToken);
        policies.Should().Contain(p => p.Name == name);
    }
}
