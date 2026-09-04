using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using McpServer.Features.McpClients;

namespace McpServer.Tests.Features.McpClients;

// FR-16, FR-05, UC-09 基本フロー 1, SC-12, ADR-0062 決定 2・3:
// 無人アカウントの `clearance` / タグが登録者の集合の部分集合であることを、**後段が**判定する。
//
// 判定そのものは `ServiceAccountAttributeSubsetTests` が器なしで固定する。ここで見るのは
// **経路**である —— 登録と差し替えの両方に効くこと、拒否が 400 であること、
// **外れた値が応答本文に載ること**（画面がそれを描くための唯一の材料である）。
[Trait("TestKind", "Integration")]
public class ServiceAccountAttributeSubsetEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // 登録者が配れる集合をヘッダで注入する（`StubRegistrarAttributeResolver`）。
    private HttpClient Registrar(string? clearance = null, string? tags = null)
    {
        var client = factory.CreateClient();
        if (clearance is not null)
            client.DefaultRequestHeaders.Add(StubRegistrarAttributeResolver.ClearanceHeader, clearance);
        if (tags is not null)
            client.DefaultRequestHeaders.Add(StubRegistrarAttributeResolver.TagsHeader, tags);
        return client;
    }

    private static RegisterMcpClientRequest ServiceAccount(
        string clientId, params (string Key, string Value)[] attributes)
        => new(clientId, clientId, "service-account",
            attributes.ToDictionary(a => a.Key, a => a.Value));

    // 受け入れ基準 1: 登録者の集合外の機密区分は 400 になり、**外れた値が名指しで**含まれる。
    [Fact]
    public async Task 登録者の集合外の機密区分は外れた値つきで拒否される()
    {
        var response = await Registrar(clearance: "public,internal").PostAsJsonAsync(
            "/mcp-clients", ServiceAccount("sa-over", ("clearance", "confidential")),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("confidential");
    }

    // 受け入れ基準 2（陽性対照）: **登録者より狭い無人アカウントは作れる。**
    [Fact]
    public async Task 登録者より狭い機密区分の無人アカウントは登録できる()
    {
        var response = await Registrar(clearance: "public,internal,confidential").PostAsJsonAsync(
            "/mcp-clients", ServiceAccount("sa-narrow", ("clearance", "internal")),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // 受け入れ基準 3: タグは**外れた値だけ**が列挙される（外れていない値を混ぜない）。
    [Fact]
    public async Task タグは外れた値だけが応答に載る()
    {
        var response = await Registrar(clearance: "public", tags: "sales,hr").PostAsJsonAsync(
            "/mcp-clients", ServiceAccount("sa-tags", ("tags", "sales,finance")),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("finance");
        body.Should().NotContain("'sales' は");
    }

    // 受け入れ基準 4: 🔴 **差し替え経路でも同じ判定・同じ文言で拒否する。**
    // 登録だけ塞いで差し替えが緩い形にしない。
    [Fact]
    public async Task 属性差し替えでも同じ判定が効く()
    {
        var client = Registrar(clearance: "public,internal");
        (await client.PostAsJsonAsync("/mcp-clients", ServiceAccount("sa-replace"),
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await client.PutAsJsonAsync("/mcp-clients/sa-replace/attributes",
            new ReplaceMcpClientAttributesRequest(
                new Dictionary<string, string> { ["clearance"] = "restricted" }),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("restricted");
    }

    // 受け入れ基準 5: **ロールと機密区分は別の軸である。** 呼び出し元は既定で `platform-admin`
    // （`TestAuthHandler`）だが、`clearance` が `internal` なら `restricted` は配れない。
    [Fact]
    public async Task システム管理者でも自分の機密区分を超えて配れない()
    {
        var response = await Registrar(clearance: "public,internal").PostAsJsonAsync(
            "/mcp-clients", ServiceAccount("sa-admin", ("clearance", "restricted")),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 受け入れ基準 6: 登録者の属性を解決できないときは配らない（deny-by-default）。
    [Fact]
    public async Task 登録者の属性を解決できないときは拒否される()
    {
        // ヘッダ無し＝スタブは `Unavailable` を返す。
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/mcp-clients", ServiceAccount("sa-unresolved", ("clearance", "public")),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("解決できませんでした");
    }

    // 🔴 陽性対照（上の対）: **対象の属性を含まない登録は、解決できなくても通る。**
    // これが無いと「認可サービスが落ちると SC-12 が全滅する」変更を入れても気づけない。
    [Fact]
    public async Task 対象属性を含まない登録は解決できなくても通る()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/mcp-clients", ServiceAccount("sa-nogoverned", ("doc_scope", "organization")),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // 受け入れ基準 9: ADR-0034 決定 9（個人資料の割当禁止）は本変更後も従来どおり効く。
    // **登録者が何を持っていようと通らない**（別の軸の絞りである）。
    [Fact]
    public async Task 個人資料の属性割当は登録者の集合に関わらず拒否される()
    {
        var response = await Registrar(clearance: "public,internal,confidential,restricted")
            .PostAsJsonAsync("/mcp-clients",
                ServiceAccount("sa-private", ("doc_scope", "private-note")),
                TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("private-note");
    }

    // 有人アカウントは本規則の対象外である（属性は利用者本人のもので解決される）。
    [Fact]
    public async Task 有人アカウントは本規則の対象外である()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/mcp-clients",
            new RegisterMcpClientRequest("agent-subset", "有人", "interactive",
                new Dictionary<string, string> { ["clearance"] = "restricted" }),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
