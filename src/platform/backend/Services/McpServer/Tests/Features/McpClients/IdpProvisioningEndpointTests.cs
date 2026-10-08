using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using McpServer.Domain.Ports;
using McpServer.Features.McpClients;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace McpServer.Tests.Features.McpClients;

// FR-16, FR-09, UC-09, SC-12, 計画 ADR-0123 決定 2・3・4, ADR-0062 決定 2・3, [[IADR-0515]] (#1786):
// SC-12 の登録・差し替えが **検証 → IdP → 登録簿** の順で書くことを、API 面から固定する。
//
// IdP はプロセス内の口（`InMemoryServiceAccountProvisioner`）であり、ここで見るのは「IdP へ何が書かれたか（書かれなかったか）」と
// 登録簿との関係である。**Keycloak への疎通は `KeycloakServiceAccountProvisionerTests`（偽の Keycloak）と稼働での実測（残余）が持つ。**
[Trait("TestKind", "Integration")]
public class IdpProvisioningEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private InMemoryServiceAccountProvisioner Idp
        => (InMemoryServiceAccountProvisioner)factory.Services.GetRequiredService<IServiceAccountProvisioner>();

    private HttpClient Registrar(string clearance = "public,internal", string? tags = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(StubRegistrarAttributeResolver.ClearanceHeader, clearance);
        if (tags is not null) client.DefaultRequestHeaders.Add(StubRegistrarAttributeResolver.TagsHeader, tags);
        return client;
    }

    private static RegisterMcpClientRequest ServiceAccount(string clientId, params (string Key, string Value)[] attributes)
        => new(clientId, clientId, "service-account", attributes.ToDictionary(a => a.Key, a => a.Value));

    // T-1786-31（AC 1 の API 面）: 無人の登録が成功したら、IdP に同じ clientId があり、割り当てた属性が入っている。登録簿はその写し。
    [Fact]
    public async Task 無人の登録はIdPへ属性を書いてから登録簿へ書く()
    {
        var response = await Registrar(tags: "sales,hr").PostAsJsonAsync("/mcp-clients",
            ServiceAccount("idp-ok", ("clearance", "internal"), ("tags", "sales")), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        Idp.Snapshot()["idp-ok"].Should().BeEquivalentTo(
            new Dictionary<string, string> { ["clearance"] = "internal", ["tags"] = "sales" });
        var view = await response.Content.ReadFromJsonAsync<McpClientView>(Ct);
        view!.Attributes.Should().BeEquivalentTo(Idp.Snapshot()["idp-ok"], "登録簿は IdP へ書いた値の写しである");
    }

    // T-1786-32（AC 2・否定形）: 登録者の集合の部分集合でない属性は、**IdP へ何も書かずに**拒否する。
    [Fact]
    public async Task 部分集合でない割当はIdPへ何も書かずに拒否する()
    {
        var response = await Registrar(clearance: "public").PostAsJsonAsync("/mcp-clients",
            ServiceAccount("idp-over", ("clearance", "confidential")), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Idp.Snapshot().Should().NotContainKey("idp-over");
    }

    // T-1786-33（AC 2・否定形）: 個人資料（`private-note`）を含む割当は、**IdP へ何も書かずに**拒否する。
    [Fact]
    public async Task 個人資料の割当はIdPへ何も書かずに拒否する()
    {
        var response = await Registrar().PostAsJsonAsync("/mcp-clients",
            ServiceAccount("idp-private", ("doc_scope", "private-note")), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Idp.Snapshot().Should().NotContainKey("idp-private");
    }

    // T-1786-34（AC 2・否定形・差し替え）: 差し替えで外れた割当は、IdP の属性を**書き換えずに**拒否する。
    [Fact]
    public async Task 差し替えで外れた割当はIdPを書き換えずに拒否する()
    {
        var client = Registrar(clearance: "public,internal");
        (await client.PostAsJsonAsync("/mcp-clients", ServiceAccount("idp-replace", ("clearance", "public")), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var over = await client.PutAsJsonAsync("/mcp-clients/idp-replace/attributes",
            new ReplaceMcpClientAttributesRequest(new Dictionary<string, string> { ["clearance"] = "confidential" }), Ct);
        var priv = await client.PutAsJsonAsync("/mcp-clients/idp-replace/attributes",
            new ReplaceMcpClientAttributesRequest(new Dictionary<string, string> { ["doc_scope"] = "private-note" }), Ct);

        over.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        priv.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Idp.Snapshot()["idp-replace"].Should().BeEquivalentTo(new Dictionary<string, string> { ["clearance"] = "public" });
    }

    // T-1786-35（陽性対照・差し替え）: 通った差し替えは IdP の属性を置き換え、登録簿も同じ値になる。
    [Fact]
    public async Task 通った差し替えはIdPと登録簿を同じ値にする()
    {
        var client = Registrar(clearance: "public,internal");
        await client.PostAsJsonAsync("/mcp-clients", ServiceAccount("idp-swap", ("clearance", "public")), Ct);

        var response = await client.PutAsJsonAsync("/mcp-clients/idp-swap/attributes",
            new ReplaceMcpClientAttributesRequest(new Dictionary<string, string> { ["clearance"] = "internal" }), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Idp.Snapshot()["idp-swap"].Should().BeEquivalentTo(new Dictionary<string, string> { ["clearance"] = "internal" });
        (await response.Content.ReadFromJsonAsync<McpClientView>(Ct))!.Attributes["clearance"].Should().Be("internal");
    }

    // T-1786-36（否定形）: IdP に入口を通らずに作られた同名のクライアントがあれば、何も書かずに拒み、登録簿にも載せない。
    [Fact]
    public async Task IdPに既にあるクライアントは登録しない()
    {
        Idp.Seed("idp-preexisting", new Dictionary<string, string> { ["clearance"] = "public" });

        var response = await Registrar().PostAsJsonAsync("/mcp-clients",
            ServiceAccount("idp-preexisting", ("clearance", "internal")), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("IdP");
        Idp.Snapshot()["idp-preexisting"]["clearance"].Should().Be("public", "入口を通らない主体へ属性を書かない");
        var list = await factory.CreateClient().GetFromJsonAsync<List<McpClientView>>("/mcp-clients", Ct);
        list.Should().NotContain(c => c.ClientId == "idp-preexisting");
    }

    // T-1786-37: 有人は IdP へ書かない（テンプレートが計画に無い。IADR-0515 決定 3・§残余）。登録簿へは従来どおり書く。
    [Fact]
    public async Task 有人の登録はIdPへ書かない()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/mcp-clients",
            new RegisterMcpClientRequest("idp-human", "有人", "interactive"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        Idp.Snapshot().Should().NotContainKey("idp-human");
    }
}

// T-1786-38（AC 4 の暫定手段・否定形）: 書き込み口が構成されていない配備は、無人の登録・差し替えを 503 で拒み、**登録簿にも書かない**
// （ADR-0123 決定 4: 入口ができるまで IdP へ属性を配らない。登録簿だけへ書く現状へは倒さない）。
[Trait("TestKind", "Integration")]
public class UnconfiguredIdpProvisioningEndpointTests : IClassFixture<UnconfiguredIdpProvisioningEndpointTests.Factory>
{
    public sealed class Factory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["McpClientProvisioning:Provider"] = "" }));
        }
    }

    private readonly Factory _factory;

    public UnconfiguredIdpProvisioningEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task 書き込み口が無ければ無人の登録を503で拒み登録簿へ書かない()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(StubRegistrarAttributeResolver.ClearanceHeader, "public");

        var response = await client.PostAsJsonAsync("/mcp-clients",
            new RegisterMcpClientRequest("no-idp", "無人", "service-account",
                new Dictionary<string, string> { ["clearance"] = "public" }), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var list = await client.GetFromJsonAsync<List<McpClientView>>("/mcp-clients", TestContext.Current.CancellationToken);
        list.Should().NotContain(c => c.ClientId == "no-idp");
        _factory.Services.GetRequiredService<IServiceAccountProvisioner>()
            .Should().BeOfType<UnconfiguredServiceAccountProvisioner>();
    }

    // 陽性対照: 有人の登録は書き込み口が無くても通る（MCP サーバーの他の機能を止めない）。
    [Fact]
    public async Task 書き込み口が無くても有人の登録は通る()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/mcp-clients",
            new RegisterMcpClientRequest("no-idp-human", "有人", "interactive"), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
