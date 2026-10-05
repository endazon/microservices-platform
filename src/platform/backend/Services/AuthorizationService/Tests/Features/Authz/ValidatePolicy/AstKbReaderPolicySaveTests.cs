using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthorizationService.Tests.Features.Authz.ResolveScope;
using AwesomeAssertions;

namespace AuthorizationService.Tests.Features.Authz.ValidatePolicy;

// FR-05, FR-09, NFR-09, 計画 ADR-0125 決定 2, [[IADR-0500]] 決定 3・4 (#1755):
// **seed の AST の KB の読み手のポリシー（文書の条件 2 キー）が、実物の保存の口を通る**ことを固定する。
//
// 🔴 seed は管理 API（`POST /authz/policies`）で投入され（[[IADR-0133]]）、投入済みの環境は `PUT /authz/policies/{id}` で
//   書き換える（IADR-0500 決定 3。運用文書の手順）。保存時の検証は「文書条件は 1 キーまで」（planning#470）を課すので、
//   例外が無いと seed の投入も本番の手順も 400 で止まる。例外の形を崩した本文（上限に confidential）は従来どおり 400 である。
[Trait("TestKind", "Integration")]
public class AstKbReaderPolicySaveTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client => factory.CreateClient();

    private static SeedScopeFixture.SeedPolicy SeedReader()
        => SeedScopeFixture.SeedPolicies().Single(p => p.UserConditions.ContainsKey("projects"));

    private static object Body(string name, Dictionary<string, List<string>> doc)
        => new { Name = name, Action = SeedReader().Action, UserConditions = SeedReader().UserConditions, DocumentConditions = doc };

    [Fact]
    public async Task Seedの読み手のポリシーは保存でき上限を広げたPUTは拒否される()
    {
        var name = $"AST の KB の読み手は AST の文書を読める-{Guid.NewGuid():N}";
        var seedDoc = SeedReader().DocumentConditions;
        seedDoc.Keys.Should().BeEquivalentTo(["project", "confidentiality"], "seed の読み手は上限つきの 2 キーである");

        var created = await Client.PostAsJsonAsync("/authz/policies", Body(name, seedDoc), TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created, "seed の投入（IADR-0133）が通らなければ dev も本番も上限を入れられない");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();

        // 投入済みの環境の書き換え（IADR-0500 決定 3）: 同じ本文の PUT は通る。
        (await Client.PutAsJsonAsync($"/authz/policies/{id}", Body(name, seedDoc), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // 🔴 上限に confidential を足した PUT は、例外の形から外れて「1 キーまで」で拒否される。
        var widened = new Dictionary<string, List<string>>
        {
            ["project"] = ["ai-stock-trading"],
            ["confidentiality"] = ["public", "internal", "confidential"],
        };
        var rejected = await Client.PutAsJsonAsync($"/authz/policies/{id}", Body(name, widened), TestContext.Current.CancellationToken);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("errors").GetProperty("errors").EnumerateArray().Select(e => e.GetString()!)
            .Should().Contain(e => e.Contains("1 つまで"));
    }
}
