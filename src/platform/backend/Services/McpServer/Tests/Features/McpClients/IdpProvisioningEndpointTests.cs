using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Infrastructure.Persistence;
using McpServer.Features.McpClients;
using McpServer.Features.McpClients.IdpReconciliation;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace McpServer.Tests.Features.McpClients;

// FR-16, FR-09, UC-09, SC-12, 計画 ADR-0123 決定 2・3・4, ADR-0062 決定 2・3, [[IADR-0516]] (#1786):
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

    // T-1786-39（否定形・監査 🔴-1）: 本入口ができる前に登録簿へ載った無人の行が、IdP の入口が作っていないクライアント
    // （例: プラットフォームの `abac-seeder`）と同名なら、差し替えは 400 で拒み、その主体の IdP の属性を書き換えない。
    [Fact]
    public async Task 差し替えは入口が作っていないクライアントの属性を書き換えない()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<McpDbContext>();
            db.Clients.Add(McpClient.Register("abac-seeder-like", "旧い行", McpClientKind.ServiceAccount,
                new Dictionary<string, string> { ["clearance"] = "public" }, EgressTier.StandardExternal, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Ct);
        }
        Idp.Seed("abac-seeder-like", new Dictionary<string, string> { ["clearance"] = "restricted" });

        var response = await Registrar(clearance: "public,internal").PutAsJsonAsync("/mcp-clients/abac-seeder-like/attributes",
            new ReplaceMcpClientAttributesRequest(new Dictionary<string, string> { ["clearance"] = "internal" }), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Idp.Snapshot()["abac-seeder-like"]["clearance"].Should().Be("restricted", "入口が作っていない主体の属性を上書きしない");
        var list = await factory.CreateClient().GetFromJsonAsync<List<McpClientView>>("/mcp-clients", Ct);
        list!.Single(c => c.ClientId == "abac-seeder-like").Attributes["clearance"].Should().Be("public", "登録簿も書かない");
    }

    // T-1786-40（Q3 の決定）: 無効化した行の差し替えで IdP に作るときは、無効のまま作る。
    [Fact]
    public async Task 無効化した行の差し替えはIdPに無効のまま作る()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<McpDbContext>();
            var row = McpClient.Register("legacy-disabled", "旧い行", McpClientKind.ServiceAccount,
                new Dictionary<string, string>(), EgressTier.StandardExternal, DateTimeOffset.UtcNow);
            row.SetEnabled(false, DateTimeOffset.UtcNow);
            db.Clients.Add(row);
            await db.SaveChangesAsync(Ct);
        }

        var response = await Registrar().PutAsJsonAsync("/mcp-clients/legacy-disabled/attributes",
            new ReplaceMcpClientAttributesRequest(new Dictionary<string, string> { ["clearance"] = "public" }), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Idp.IsEnabled("legacy-disabled").Should().BeFalse();
    }

    private async Task AddLegacyRow(string clientId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<McpDbContext>();
        db.Clients.Add(McpClient.Register(clientId, "旧い行", McpClientKind.ServiceAccount,
            new Dictionary<string, string> { ["clearance"] = "public" }, EgressTier.StandardExternal, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
    }

    private async Task<bool> RegistryEnabled(string clientId)
        => (await factory.CreateClient().GetFromJsonAsync<List<McpClientView>>("/mcp-clients", Ct))!
            .Single(c => c.ClientId == clientId).Enabled;

    // C-55（#1829 受け入れ基準 1・IADR-0516 決定 4a）: 無人のクライアントを無効化すると IdP のクライアントも無効になり、
    // 再有効化で両方とも有効へ戻る。
    [Fact]
    public async Task 無人の無効化と再有効化はIdPのクライアントのenabledへ写す()
    {
        (await Registrar().PostAsJsonAsync("/mcp-clients", ServiceAccount("idp-toggle", ("clearance", "public")), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var admin = factory.CreateClient();

        var disabled = await admin.PostAsync("/mcp-clients/idp-toggle/disable", null, Ct);

        disabled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await disabled.Content.ReadFromJsonAsync<McpClientView>(Ct))!.Enabled.Should().BeFalse();
        Idp.IsEnabled("idp-toggle").Should().BeFalse("無効化を IdP のクライアントへ写す");

        var enabled = await admin.PostAsync("/mcp-clients/idp-toggle/enable", null, Ct);

        enabled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RegistryEnabled("idp-toggle")).Should().BeTrue();
        Idp.IsEnabled("idp-toggle").Should().BeTrue();
    }

    // C-56（#1829 受け入れ基準 2・否定形）: 入口の印が無い同名のクライアント（例 `abac-seeder`）は、無効化の経路から変えない。
    // 無効化は登録簿だけを無効にして 200、再有効化は接続を開かずに 400（登録簿も書かない）。IdP に無い行は登録簿だけを切り替える。
    [Fact]
    public async Task 入口の印が無いクライアントは無効化の経路から変えない()
    {
        await AddLegacyRow("seeder-like");
        Idp.Seed("seeder-like");
        await AddLegacyRow("legacy-absent");
        var admin = factory.CreateClient();

        var disabled = await admin.PostAsync("/mcp-clients/seeder-like/disable", null, Ct);
        disabled.StatusCode.Should().Be(HttpStatusCode.OK, "即時の接続拒否（登録簿）は止めない");
        (await RegistryEnabled("seeder-like")).Should().BeFalse();
        Idp.IsEnabled("seeder-like").Should().BeTrue("プラットフォームのクライアントを止めない");

        var enabled = await admin.PostAsync("/mcp-clients/seeder-like/enable", null, Ct);
        enabled.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await enabled.Content.ReadAsStringAsync(Ct)).Should().Contain("接続を開きません");
        (await RegistryEnabled("seeder-like")).Should().BeFalse("入口を通らない主体へ接続を開かない");
        Idp.IsEnabled("seeder-like").Should().BeTrue();

        (await admin.PostAsync("/mcp-clients/legacy-absent/disable", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RegistryEnabled("legacy-absent")).Should().BeFalse();
        (await admin.PostAsync("/mcp-clients/legacy-absent/enable", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RegistryEnabled("legacy-absent")).Should().BeTrue();
        Idp.Snapshot().Should().NotContainKey("legacy-absent", "IdP に無いクライアントを無効化の経路で作らない");
    }

    private static RegisterMcpClientRequest Interactive(string clientId, params string[] redirectUris)
        => new(clientId, clientId, "interactive", RedirectUris: [.. redirectUris]);

    // C-60（#1844 AC1・計画 ADR-0134 決定 1）: 有人の登録は IdP に公開クライアントを作り（入力のリダイレクト URI のまま）、それから登録簿へ書く。
    // 旧 T-1786-37「有人は IdP へ書かない」（IADR-0516 決定 3 の既知の逸脱）を置き換えた。
    [Fact]
    public async Task 有人の登録はIdPに公開クライアントを作ってから登録簿へ書く()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/mcp-clients",
            Interactive("idp-human", "https://agent.example.test/cb", "http://127.0.0.1/cb"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        Idp.RedirectUrisOf("idp-human").Should().Equal("https://agent.example.test/cb", "http://127.0.0.1/cb");
        Idp.IsEnabled("idp-human").Should().BeTrue();
        (await response.Content.ReadFromJsonAsync<McpClientView>(Ct))!.Kind.Should().Be("interactive");
    }

    // C-61（#1844 AC1b・否定形）: 規則に外れたリダイレクト URI（ワイルドカード）は 400 で、**IdP にも登録簿にも何も作らない**。
    [Fact]
    public async Task 有人の不正なリダイレクトURIはIdPへ何も書かずに拒否する()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/mcp-clients", Interactive("idp-human-wild", "https://agent.example.test/*"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("ワイルドカード");
        Idp.Snapshot().Should().NotContainKey("idp-human-wild");
        (await client.GetFromJsonAsync<List<McpClientView>>("/mcp-clients", Ct))!
            .Should().NotContain(c => c.ClientId == "idp-human-wild");
    }

    // C-62（#1844 AC1・否定形）: IdP に同じ clientId の（入口を通らない）クライアントが在れば、有人も 400 で登録簿へ書かない。
    [Fact]
    public async Task 有人もIdPに在る同名のクライアントは登録しない()
    {
        Idp.Seed("platform-like-human");

        var response = await factory.CreateClient().PostAsJsonAsync("/mcp-clients",
            Interactive("platform-like-human", "https://agent.example.test/cb"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("有人の MCP クライアントとして登録しません");
        Idp.RedirectUrisOf("platform-like-human").Should().BeNull("入口を通らないクライアントを書き換えない");
    }

    // C-63（#1844・IADR-0516 決定 4a の有人への拡張）: 有人の行の無効化・再有効化も、IdP の公開クライアントの enabled へ写す。
    [Fact]
    public async Task 有人の無効化と再有効化もIdPのenabledへ写す()
    {
        var admin = factory.CreateClient();
        (await admin.PostAsJsonAsync("/mcp-clients", Interactive("idp-human-toggle", "https://agent.example.test/cb"), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        (await admin.PostAsync("/mcp-clients/idp-human-toggle/disable", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        Idp.IsEnabled("idp-human-toggle").Should().BeFalse();
        (await admin.PostAsync("/mcp-clients/idp-human-toggle/enable", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        Idp.IsEnabled("idp-human-toggle").Should().BeTrue();
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

    // C-57（#1829）: 書き込み口が無くても無効化は登録簿だけで通る（即時の接続拒否を止めない）。再有効化は接続を開く側なので 503 で、登録簿も書かない。
    [Fact]
    public async Task 書き込み口が無くても無効化は通り再有効化は503()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<McpDbContext>();
            db.Clients.Add(McpClient.Register("no-idp-toggle", "無人", McpClientKind.ServiceAccount,
                new Dictionary<string, string>(), EgressTier.StandardExternal, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var client = _factory.CreateClient();

        (await client.PostAsync("/mcp-clients/no-idp-toggle/disable", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/mcp-clients/no-idp-toggle/enable", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var list = await client.GetFromJsonAsync<List<McpClientView>>("/mcp-clients", TestContext.Current.CancellationToken);
        list!.Single(c => c.ClientId == "no-idp-toggle").Enabled.Should().BeFalse();
    }

    // ［#1844］有人も書き込み口が無ければ 503 で、登録簿へ書かない（従前の陽性対照「有人は通る」は、IADR-0516 決定 3 の逸脱そのものだった。
    // 有人だけを登録簿へ書く経路へは倒さない —— 計画 ADR-0134 決定 3 の暫定手段は「IdP に作らない」であり、登録簿にだけ在る有人の行は
    // 計画とのずれを増やすだけである）。
    [Fact]
    public async Task 書き込み口が無ければ有人の登録も503で登録簿へ書かない()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/mcp-clients",
            new RegisterMcpClientRequest("no-idp-human", "有人", "interactive", RedirectUris: ["https://agent.example.test/callback"]),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await client.GetFromJsonAsync<List<McpClientView>>("/mcp-clients", TestContext.Current.CancellationToken))!
            .Should().NotContain(c => c.ClientId == "no-idp-human");
    }
}

// C-57（#1829 受け入れ基準 3・IADR-0516 決定 4a）: IdP への有効・無効の写しが失敗しても、**登録簿の無効化は取り消さない**
// （即時の接続拒否を優先する。200）。食い違い（登録簿は無効・IdP は有効）は照合が `enabled_differs` として拾う。
// 再有効化（開く側）は IdP が先なので 502 で、登録簿は無効のまま。写せるようになってから無効化をもう一度送れば写し直す。
[Trait("TestKind", "Integration")]
public class IdpEnabledMirrorFailureEndpointTests : IClassFixture<IdpEnabledMirrorFailureEndpointTests.Factory>
{
    public sealed class FailingEnabledProvisioner(InMemoryServiceAccountProvisioner inner)
        : IServiceAccountProvisioner, IServiceAccountDirectory
    {
        public InMemoryServiceAccountProvisioner Inner { get; } = inner;
        public volatile bool Fail;

        public Task<IdpWrite> CreateAsync(string clientId, string displayName,
            IReadOnlyDictionary<string, string> attributes, CancellationToken ct) => Inner.CreateAsync(clientId, displayName, attributes, ct);

        public Task<IdpWrite> CreatePublicClientAsync(string clientId, string displayName,
            IReadOnlyList<string> redirectUris, CancellationToken ct)
            => Inner.CreatePublicClientAsync(clientId, displayName, redirectUris, ct);

        public Task<IdpWrite> ReplaceAttributesAsync(string clientId, string displayName,
            IReadOnlyDictionary<string, string> attributes, bool enabled, CancellationToken ct)
            => Inner.ReplaceAttributesAsync(clientId, displayName, attributes, enabled, ct);

        public Task<IdpWrite> SetEnabledAsync(string clientId, bool enabled, CancellationToken ct)
            => Fail
                ? throw new IdpProvisioningException(IdpProvisioningFailure.Failed, "IdP（Keycloak）へ到達できない。")
                : Inner.SetEnabledAsync(clientId, enabled, ct);

        public Task UndoAsync(IdpWrite write, CancellationToken ct) => Inner.UndoAsync(write, ct);

        public Task<IReadOnlyList<IdpClientEntry>> ListClientsAsync(CancellationToken ct) => Inner.ListClientsAsync(ct);

        public Task<IReadOnlyDictionary<string, string>?> ReadServiceAccountAttributesAsync(string clientId, CancellationToken ct)
            => Inner.ReadServiceAccountAttributesAsync(clientId, ct);
    }

    public sealed class Factory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceAccountProvisioner>();
                services.AddSingleton(sp => new FailingEnabledProvisioner(
                    sp.GetRequiredService<InMemoryServiceAccountProvisioner>()));
                services.AddSingleton<IServiceAccountProvisioner>(sp => sp.GetRequiredService<FailingEnabledProvisioner>());
            });
        }
    }

    private readonly Factory _factory;

    public IdpEnabledMirrorFailureEndpointTests(Factory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IdPへの写しが失敗しても登録簿の無効化は取り消さず照合が拾う()
    {
        var idp = _factory.Services.GetRequiredService<FailingEnabledProvisioner>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(StubRegistrarAttributeResolver.ClearanceHeader, "public");
        (await client.PostAsJsonAsync("/mcp-clients", new RegisterMcpClientRequest("mirror-fail", "無人", "service-account",
            new Dictionary<string, string> { ["clearance"] = "public" }), Ct)).StatusCode.Should().Be(HttpStatusCode.Created);

        idp.Fail = true;
        var disabled = await client.PostAsync("/mcp-clients/mirror-fail/disable", null, Ct);

        disabled.StatusCode.Should().Be(HttpStatusCode.OK, "即時の接続拒否（登録簿の無効化）を優先する");
        (await disabled.Content.ReadFromJsonAsync<McpClientView>(Ct))!.Enabled.Should().BeFalse();
        (await Registry(client)).Should().BeFalse("登録簿の無効化は取り消さない");
        idp.Inner.IsEnabled("mirror-fail").Should().BeTrue("写せていない");

        using (var scope = _factory.Services.CreateScope())
        {
            var drifts = await scope.ServiceProvider.GetRequiredService<IdpReconciliationCheck>().RunAsync(Ct);
            drifts.Should().Contain(new IdpDrift("mirror-fail", IdpDriftKind.EnabledDiffers), "食い違いは照合が拾う");
        }

        var enabled = await client.PostAsync("/mcp-clients/mirror-fail/enable", null, Ct);
        enabled.StatusCode.Should().Be(HttpStatusCode.BadGateway, "開く側は IdP が先。書けなければ登録簿へ書かない");
        (await Registry(client)).Should().BeFalse();

        idp.Fail = false;
        (await client.PostAsync("/mcp-clients/mirror-fail/disable", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        idp.Inner.IsEnabled("mirror-fail").Should().BeFalse("同じ操作をもう一度送れば写し直す");
    }

    private static async Task<bool> Registry(HttpClient client)
        => (await client.GetFromJsonAsync<List<McpClientView>>("/mcp-clients", Ct))!.Single(c => c.ClientId == "mirror-fail").Enabled;
}
