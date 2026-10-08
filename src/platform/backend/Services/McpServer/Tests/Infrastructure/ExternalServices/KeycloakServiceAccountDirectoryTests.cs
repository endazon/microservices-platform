using AwesomeAssertions;
using McpServer.Domain.Ports;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging.Abstractions;
using static McpServer.Tests.Infrastructure.ExternalServices.KeycloakServiceAccountProvisionerTests;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// FR-16, SC-12, 計画 ADR-0123 決定 2, [[IADR-0516]] 決定 5（2026-10-09 追記 / #1818）:
// 照合が IdP（Keycloak Admin REST）を**読む**口。偽の Keycloak（`FakeKeycloak`）に対して、何をどう読むか・読めないときに何を投げるかを固定する。
// 🔴 **緑は「稼働の Keycloak が一覧にクライアント属性を載せる」ことを意味しない。** それは integration-stack の門（M7）が測る。
[Trait("TestKind", "Unit")]
public class KeycloakServiceAccountDirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KeycloakServiceAccountProvisioner Directory(FakeKeycloak keycloak)
        => new(new StubFactory(keycloak), new ServiceAccountProvisioningOptions
        {
            BaseUrl = "https://auth.example.test",
            Realm = "platform",
            ClientId = "mcp-client-admin",
            ClientSecret = "injected-at-deploy-time",
        }, TimeProvider.System, NullLogger<KeycloakServiceAccountProvisioner>.Instance);

    private static Dictionary<string, string> Attrs(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    // C-36: 一覧は入口の印の有無を返し、頁をまたいで読み切る（100 件ごと）。
    [Fact]
    public async Task 一覧は入口の印の有無を返し頁をまたいで読み切る()
    {
        var keycloak = new FakeKeycloak();
        var directory = Directory(keycloak);
        await directory.CreateAsync("agent-a", "A", Attrs(("clearance", "public")), Ct);
        for (var i = 0; i < 150; i++) keycloak.SeedClient($"platform-{i:000}", []);

        var clients = await directory.ListClientsAsync(Ct);

        clients.Should().HaveCount(151);
        clients.Single(c => c.ClientId == "agent-a").Managed.Should().BeTrue("入口が作ったクライアントには印がある");
        clients.Where(c => c.ClientId != "agent-a").Should().OnlyContain(c => !c.Managed, "入口を通らないクライアントに印は無い");
        keycloak.Requests.Where(r => r.Path.StartsWith("admin/realms/platform/clients?first=", StringComparison.Ordinal))
            .Select(r => r.Path).Should().Equal(
                "admin/realms/platform/clients?first=0&max=100",
                "admin/realms/platform/clients?first=100&max=100");
    }

    // C-37: サービスアカウントの属性は、認可サービスと同じ照会（利用者名の完全一致）で読み、集合値を 1 つの値へ戻す。
    [Fact]
    public async Task 属性は認可サービスと同じ照会で読み集合値を戻す()
    {
        var keycloak = new FakeKeycloak();
        var directory = Directory(keycloak);
        await directory.CreateAsync("Agent-B", "B", Attrs(("clearance", "internal"), ("tags", "sales,hr")), Ct);
        keycloak.Requests.Clear();

        var attributes = await directory.ReadServiceAccountAttributesAsync("Agent-B", Ct);

        attributes.Should().NotBeNull();
        attributes!["clearance"].Should().Be("internal");
        attributes["tags"].Split(',').Should().BeEquivalentTo("sales", "hr");
        keycloak.Requests.Should().Contain(r => r.Path ==
            "admin/realms/platform/users?username=service-account-agent-b&exact=true&briefRepresentation=false&max=2");
    }

    // C-37（否定形）: 照会で利用者が引けなければ null（判定ではその主体は名簿に居ない）。
    [Fact]
    public async Task 照会で引けなければnullを返す()
    {
        var keycloak = new FakeKeycloak { LookupHidesServiceAccounts = true };
        keycloak.SeedClient("agent-c", []);

        (await Directory(keycloak).ReadServiceAccountAttributesAsync("agent-c", Ct)).Should().BeNull();
    }

    // C-38（否定形）: 読むだけで書かない（受けた管理要求はすべて GET）。
    [Fact]
    public async Task 読み取りは管理要求をGETしか送らない()
    {
        var keycloak = new FakeKeycloak();
        keycloak.SeedClient("agent-d", new Dictionary<string, string[]> { ["clearance"] = ["public"] });
        var directory = Directory(keycloak);

        await directory.ListClientsAsync(Ct);
        await directory.ReadServiceAccountAttributesAsync("agent-d", Ct);

        keycloak.Requests.Where(r => r.Path.StartsWith("admin/", StringComparison.Ordinal))
            .Should().OnlyContain(r => r.Method == "GET");
    }

    // C-39: 一覧が上限（100 頁 ＝ 1 万件）を超えたら、読み切らずに失敗する（途中までの一覧で孤児を数えない）。
    [Fact]
    public async Task 一覧が上限を超えたら失敗する()
    {
        var keycloak = new FakeKeycloak { EndlessClientList = true };

        var act = () => Directory(keycloak).ListClientsAsync(Ct);

        (await act.Should().ThrowAsync<IdpProvisioningException>())
            .Which.Failure.Should().Be(IdpProvisioningFailure.Failed);
        keycloak.Requests.Count(r => r.Path.StartsWith("admin/realms/platform/clients?first=", StringComparison.Ordinal))
            .Should().Be(100, "上限の頁数で止まる（無限に読まない）");
    }

    // C-40: 要求の時間切れ（HttpClient の Timeout）は Failed。呼び出し元の取り消し（停止・照合の期限）は取り消しのまま外へ出す。
    [Fact]
    public async Task 時間切れはFailedで呼び出し元の取り消しは取り消しのまま()
    {
        var timedOut = () => Directory(new FakeKeycloak { TimeoutOnClientList = true }).ListClientsAsync(Ct);
        (await timedOut.Should().ThrowAsync<IdpProvisioningException>())
            .Which.Failure.Should().Be(IdpProvisioningFailure.Failed);

        using var cts = new CancellationTokenSource();
        var pending = Directory(new FakeKeycloak { HangOnClientList = true }).ListClientsAsync(cts.Token);
        await cts.CancelAsync();
        var canceled = () => pending;
        await canceled.Should().ThrowAsync<OperationCanceledException>("取り消しを「IdP へ届かない」に畳まない");
    }
}
