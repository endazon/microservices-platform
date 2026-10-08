using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using McpServer.Domain.Ports;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// FR-16, SC-12, 計画 ADR-0123 決定 1・2・フォローアップ 1・3, [[IADR-0515]] 決定 1・3・4・6 (#1786):
// SC-12 の IdP への書き込み口（Keycloak Admin REST）。
//
// 🔴 **Keycloak は状態を持つ偽物（`FakeKeycloak`）で置き換える。** 稼働の Keycloak での疎通は未検証であり（IADR-0515 §残余）、
// ここで固定するのは「どの順で何を送り、失敗したら何を戻すか」である。**緑は「実 IdP へ反映できる」を意味しない。**
[Trait("TestKind", "Unit")]
public class KeycloakServiceAccountProvisionerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceAccountProvisioningOptions Options => new()
    {
        BaseUrl = "https://auth.example.test",
        Realm = "platform",
        ClientId = "mcp-client-admin",
        ClientSecret = "injected-at-deploy-time",
    };

    private static KeycloakServiceAccountProvisioner Provisioner(FakeKeycloak keycloak)
        => new(new StubFactory(keycloak), Options, TimeProvider.System,
            NullLogger<KeycloakServiceAccountProvisioner>.Instance);

    private static Dictionary<string, string> Attrs(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    // T-1786-01: 登録で機密クライアント（サービスアカウントつき）を作り、属性をその利用者属性として書く。
    [Fact]
    public async Task 登録で機密クライアントを作り属性をサービスアカウントの利用者属性として書く()
    {
        var keycloak = new FakeKeycloak();

        var written = await Provisioner(keycloak).CreateAsync(
            "Agent-X", "エージェント X", Attrs(("clearance", "internal"), ("tags", "sales,hr")), Ct);

        written.Kind.Should().Be(IdpWriteKind.Created);
        var client = keycloak.Clients.Should().ContainSingle().Subject;
        client.ClientId.Should().Be("Agent-X");
        client.Representation["publicClient"]!.GetValue<bool>().Should().BeFalse("機密クライアントである");
        client.Representation["serviceAccountsEnabled"]!.GetValue<bool>().Should().BeTrue();
        client.Representation["standardFlowEnabled"]!.GetValue<bool>().Should().BeFalse("人の流れは閉じる");
        client.Representation["directAccessGrantsEnabled"]!.GetValue<bool>().Should().BeFalse("人の流れは閉じる");
        client.Representation["implicitFlowEnabled"]!.GetValue<bool>().Should().BeFalse("人の流れは閉じる");

        var user = keycloak.Users[client.ServiceAccountUserId];
        user.Username.Should().Be("service-account-agent-x", "Keycloak はサービスアカウントの利用者名を小文字で持つ");
        user.Attributes["clearance"].Should().Equal("internal");
        user.Attributes["tags"].Should().Equal("sales", "hr");

        // `PUT /users/{id}` は部分更新ではない（IADR-0329 の実測）。現在の表現を送り返し、サーバ計算の値は送らない。
        var put = keycloak.Requests.Single(r => r.Method == "PUT").Body!;
        put.Should().Contain("keep-me", "read-modify-write でないと送らなかった項目が消える");
        put.Should().NotContain("\"access\"", "サーバ計算の値は送り返さない");
    }

    // T-1786-02（ADR-0123 フォローアップ 3）: 書く前に、認可サービスと同じ照会（`username=service-account-<client>&exact=true`）で
    // サービスアカウントの利用者が引けることを確かめる。
    [Fact]
    public async Task 書く前に認可サービスと同じ照会でサービスアカウントの利用者を引き直す()
    {
        var keycloak = new FakeKeycloak();

        await Provisioner(keycloak).CreateAsync("agent-y", "Y", Attrs(("clearance", "public")), Ct);

        var lookup = keycloak.Requests.FindIndex(r =>
            r.Method == "GET" && r.Path == "admin/realms/platform/users?username=service-account-agent-y&exact=true&briefRepresentation=false&max=2");
        var put = keycloak.Requests.FindIndex(r => r.Method == "PUT");
        lookup.Should().BeGreaterThanOrEqualTo(0, "認可サービスの照会と同じ形で引く");
        put.Should().BeGreaterThan(lookup, "照会が通ってから書く");
        keycloak.Requests.Where(r => r.Path.StartsWith("admin/")).Should()
            .OnlyContain(r => r.Authorization == "Bearer admin-token");
    }

    // T-1786-03（否定形）: 照会がサービスアカウントの利用者を返さなければ、**属性を書かずに**作ったクライアントを消して失敗する。
    [Fact]
    public async Task 照会でサービスアカウントが引けなければ属性を書かずにクライアントを消す()
    {
        var keycloak = new FakeKeycloak { LookupHidesServiceAccounts = true };

        var act = () => Provisioner(keycloak).CreateAsync("agent-z", "Z", Attrs(("clearance", "public")), Ct);

        (await act.Should().ThrowAsync<IdpProvisioningException>())
            .Which.Failure.Should().Be(IdpProvisioningFailure.Failed);
        keycloak.Requests.Should().NotContain(r => r.Method == "PUT", "判定に使われない属性は書かない");
        keycloak.Clients.Should().BeEmpty("作りかけのクライアントは消す（補償）");
    }

    // T-1786-04（否定形）: 同じ clientId のクライアントが IdP に既に在れば、**何も書かない**（入口を通らない主体へ属性を書かない）。
    [Fact]
    public async Task 既にあるクライアントへは何も書かない()
    {
        var keycloak = new FakeKeycloak();
        keycloak.SeedClient("identity-admin", new Dictionary<string, string[]>());

        var written = await Provisioner(keycloak).CreateAsync("identity-admin", "既存", Attrs(("clearance", "internal")), Ct);

        written.Kind.Should().Be(IdpWriteKind.AlreadyExists);
        keycloak.Requests.Should().NotContain(r => r.Method == "PUT");
        keycloak.Requests.Should().NotContain(r => r.Method == "DELETE", "入口が作っていないクライアントを消さない");
        keycloak.Users.Values.Single().Attributes.Should().BeEmpty();
    }

    // T-1786-05: realm が unmanaged 属性を黙って捨てたら（204 のまま読み戻せない）失敗にし、クライアントを消す。
    [Fact]
    public async Task 書いた属性が読み戻せなければ失敗にしてクライアントを消す()
    {
        var keycloak = new FakeKeycloak { DropAttributesOnPut = true };

        var act = () => Provisioner(keycloak).CreateAsync("agent-d", "D", Attrs(("clearance", "internal")), Ct);

        await act.Should().ThrowAsync<IdpProvisioningException>();
        keycloak.Clients.Should().BeEmpty();
    }

    // T-1786-06: 差し替えは書く前の属性を返し、取り消しでそれを書き戻す（補償）。
    [Fact]
    public async Task 差し替えは元の属性を返し取り消しで書き戻す()
    {
        var keycloak = new FakeKeycloak();
        var provisioner = Provisioner(keycloak);
        await provisioner.CreateAsync("agent-r", "R", Attrs(("clearance", "public")), Ct);

        var written = await provisioner.ReplaceAttributesAsync("agent-r", "R", Attrs(("clearance", "internal")), Ct);

        written.Kind.Should().Be(IdpWriteKind.Updated);
        written.PreviousAttributes.Should().BeEquivalentTo(Attrs(("clearance", "public")));
        keycloak.ServiceAccountOf("agent-r").Attributes["clearance"].Should().Equal("internal");

        await provisioner.UndoAsync(written, Ct);

        keycloak.ServiceAccountOf("agent-r").Attributes["clearance"].Should().Equal("public");
    }

    // T-1786-07: 差し替えで IdP にクライアントが無ければ（本入口ができる前の登録簿の行）作ってから書く。取り消しは削除。
    [Fact]
    public async Task 差し替えでIdPに無ければ作り取り消しで消す()
    {
        var keycloak = new FakeKeycloak();
        var provisioner = Provisioner(keycloak);

        var written = await provisioner.ReplaceAttributesAsync("legacy", "旧", Attrs(("clearance", "public")), Ct);

        written.Kind.Should().Be(IdpWriteKind.Created);
        keycloak.ServiceAccountOf("legacy").Attributes["clearance"].Should().Equal("public");

        await provisioner.UndoAsync(written, Ct);

        keycloak.Clients.Should().BeEmpty();
    }

    // T-1786-08: 属性は丸ごと置き換える（この入口だけが書く）。差し替えで外したキーは IdP からも消える。
    [Fact]
    public async Task 差し替えは属性を丸ごと置き換える()
    {
        var keycloak = new FakeKeycloak();
        var provisioner = Provisioner(keycloak);
        await provisioner.CreateAsync("agent-w", "W", Attrs(("clearance", "public"), ("tags", "sales")), Ct);

        await provisioner.ReplaceAttributesAsync("agent-w", "W", Attrs(("clearance", "internal")), Ct);

        keycloak.ServiceAccountOf("agent-w").Attributes.Keys.Should().Equal("clearance");
    }

    // T-1786-09: IdP へ到達できなければ Failed（呼び出し元は 502 を返し、登録簿へ書かない）。
    [Fact]
    public async Task 到達できなければFailedを投げる()
    {
        var keycloak = new FakeKeycloak { Unreachable = true };

        var act = () => Provisioner(keycloak).CreateAsync("agent-u", "U", Attrs(("clearance", "public")), Ct);

        (await act.Should().ThrowAsync<IdpProvisioningException>())
            .Which.Failure.Should().Be(IdpProvisioningFailure.Failed);
    }

    // T-1786-10: 集合値キーの比較は順序を問わない（読み戻しの突合で偽の失敗を出さない）。
    [Fact]
    public void 集合値キーは順序を問わずに突き合わせる()
    {
        KeycloakServiceAccountProvisioner.SameAttributes(
                Attrs(("tags", "sales,hr"), ("clearance", "internal")),
                Attrs(("tags", "hr,sales"), ("clearance", "internal")))
            .Should().BeTrue();
        KeycloakServiceAccountProvisioner.SameAttributes(
                Attrs(("clearance", "internal")), Attrs(("clearance", "confidential")))
            .Should().BeFalse();
    }

    // ── 状態を持つ偽の Keycloak ──────────────────────────────────────────────

    internal sealed record Recorded(string Method, string Path, string? Body, string? Authorization);

    internal sealed class FakeClient(string id, string clientId, string serviceAccountUserId, JsonObject representation)
    {
        public string Id { get; } = id;
        public string ClientId { get; } = clientId;
        public string ServiceAccountUserId { get; } = serviceAccountUserId;
        public JsonObject Representation { get; } = representation;
    }

    internal sealed class FakeUser(string id, string username)
    {
        public string Id { get; } = id;
        public string Username { get; } = username;
        public Dictionary<string, string[]> Attributes { get; set; } = [];
    }

    internal sealed class FakeKeycloak : HttpMessageHandler
    {
        private const string Admin = "admin/realms/platform/";
        private int _sequence;

        public List<Recorded> Requests { get; } = [];
        public List<FakeClient> Clients { get; } = [];
        public Dictionary<string, FakeUser> Users { get; } = new(StringComparer.Ordinal);
        public bool LookupHidesServiceAccounts { get; init; }
        public bool DropAttributesOnPut { get; init; }
        public bool Unreachable { get; init; }

        public void SeedClient(string clientId, Dictionary<string, string[]> attributes)
        {
            var client = NewClient(clientId, new JsonObject { ["clientId"] = clientId });
            Users[client.ServiceAccountUserId].Attributes = attributes;
        }

        public FakeUser ServiceAccountOf(string clientId)
            => Users[Clients.Single(c => c.ClientId == clientId).ServiceAccountUserId];

        private FakeClient NewClient(string clientId, JsonObject representation)
        {
            var n = ++_sequence;
            var client = new FakeClient($"c{n}", clientId, $"u{n}", representation);
            Clients.Add(client);
            Users[client.ServiceAccountUserId] = new FakeUser(client.ServiceAccountUserId,
                "service-account-" + clientId.ToLowerInvariant());
            return client;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Recorded(request.Method.Method, path, body, request.Headers.Authorization?.ToString()));
            if (Unreachable) throw new HttpRequestException("connection refused");

            var method = request.Method.Method;
            if (method == "POST" && path == "realms/platform/protocol/openid-connect/token")
                return Ok("""{"access_token":"admin-token","expires_in":300}""");

            if (method == "POST" && path == Admin + "clients")
            {
                var rep = JsonNode.Parse(body!)!.AsObject();
                var clientId = rep["clientId"]!.GetValue<string>();
                if (Clients.Any(c => c.ClientId == clientId)) return Status(HttpStatusCode.Conflict);
                var created = NewClient(clientId, rep);
                var response = Status(HttpStatusCode.Created);
                response.Headers.Location = new Uri($"https://auth.example.test/{Admin}clients/{created.Id}");
                return response;
            }

            if (method == "GET" && path.StartsWith(Admin + "clients?clientId=", StringComparison.Ordinal))
            {
                var clientId = Uri.UnescapeDataString(path[(Admin + "clients?clientId=").Length..]);
                return Ok(JsonSerializer.Serialize(Clients.Where(c => c.ClientId == clientId)
                    .Select(c => new { id = c.Id, clientId = c.ClientId })));
            }

            if (method == "GET" && path.StartsWith(Admin + "clients/", StringComparison.Ordinal)
                && path.EndsWith("/service-account-user", StringComparison.Ordinal))
            {
                var id = path[(Admin + "clients/").Length..^"/service-account-user".Length];
                var client = Clients.SingleOrDefault(c => c.Id == id);
                return client is null ? Status(HttpStatusCode.NotFound) : Ok(UserJson(Users[client.ServiceAccountUserId]));
            }

            if (method == "DELETE" && path.StartsWith(Admin + "clients/", StringComparison.Ordinal))
            {
                var id = path[(Admin + "clients/").Length..];
                var client = Clients.SingleOrDefault(c => c.Id == id);
                if (client is null) return Status(HttpStatusCode.NotFound);
                Clients.Remove(client);
                Users.Remove(client.ServiceAccountUserId);
                return Status(HttpStatusCode.NoContent);
            }

            if (method == "GET" && path.StartsWith(Admin + "users?username=", StringComparison.Ordinal))
            {
                var query = path[(Admin + "users?username=").Length..];
                var username = Uri.UnescapeDataString(query[..query.IndexOf('&')]);
                var matched = LookupHidesServiceAccounts
                    ? []
                    : Users.Values.Where(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)).ToList();
                return Ok("[" + string.Join(",", matched.Select(UserJson)) + "]");
            }

            if (path.StartsWith(Admin + "users/", StringComparison.Ordinal))
            {
                var id = path[(Admin + "users/").Length..];
                if (!Users.TryGetValue(id, out var user)) return Status(HttpStatusCode.NotFound);
                if (method == "GET") return Ok(UserJson(user));
                if (method == "PUT")
                {
                    if (!DropAttributesOnPut)
                        user.Attributes = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                            JsonNode.Parse(body!)!["attributes"]!.ToJsonString()) ?? [];
                    return Status(HttpStatusCode.NoContent);
                }
            }

            return Status(HttpStatusCode.NotFound);
        }

        private static string UserJson(FakeUser user) => JsonSerializer.Serialize(new
        {
            id = user.Id,
            username = user.Username,
            enabled = true,
            firstName = "keep-me",
            access = new { manage = true },
            attributes = user.Attributes,
        });

        private static HttpResponseMessage Ok(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Status(HttpStatusCode status)
            => new(status) { Content = new StringContent("") };
    }

    private sealed class StubFactory(FakeKeycloak handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(Options.BaseUrl.TrimEnd('/') + "/"),
        };
    }
}
