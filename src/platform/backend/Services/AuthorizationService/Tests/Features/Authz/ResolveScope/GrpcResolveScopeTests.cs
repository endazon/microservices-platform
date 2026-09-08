using System.Net;
using System.Net.Http.Json;
using AuthorizationService.Domain;
using AuthorizationService.Features.Authz.ResolveScope;
using AuthorizationService.Infrastructure.Persistence;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Contracts.Grpc.Authz.V1;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using AuthorizationService.Tests.Grpc;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace AuthorizationService.Tests.Features.Authz.ResolveScope;

// FR-05, NFR-09, NFR-16, ADR-0004, ADR-0029, ADR-0075, IADR-0379 (#1201):
// east-west gRPC の参照実装（BFF → AuthorizationService `AuthzScope/Resolve`）を**実 Kestrel の h2c ポート**で
// 往復し、s2s トークンの検証と ABAC の deny-by-default が gRPC 経路でも保たれることを固定する。
//
// 陽性対照（T-01）と陰性対照（T-03 / T-04 / T-05）を同じ器で対にする —— 「拒否された」だけでは
// 器が壊れているのか認可が効いているのか区別できない。
//
// [[IADR-0401]] (#1255): 🔴 **器は `GrpcServerCollection` が持つ（`IClassFixture` ではない）。**
// `GrpcTestConfiguration` は h2c ポートを**プロセスで 1 つだけ**選ぶため、gRPC のテストクラスが
// 2 つ以上になると器をクラスごとに作れない（2 つ目の Kestrel が同じポートへ bind できない）。
// 名簿の面（`UserDirectory`）が増えたので、器の共有と直列化を同じコレクションで満たす
// （LlmGateway の `SharedMeterCollection` と同型）。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcResolveScopeTests
{
    private const string ServiceSubject = "service-account-bff";
    private readonly GrpcKestrelFactory _factory;

    public GrpcResolveScopeTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
        SeedPolicyOnce();
        // 🔴 **属性は IdP 側にある**（計画 ADR-0088 決定 1 / [[IADR-0413]] / #1333）——
        // 要求本文の `user_attributes` は評価に用いられない。本文にも同じ値を載せているのは、
        // **載せても結果が変わらない**（＝主張が効いていない）ことを T-07 の同値で示すためである。
        _factory.Identity.Attributes["alice"] = new Dictionary<string, string>
        {
            ["department"] = "engineering",
            ["clearance"] = "internal",
        };
    }

    // department=engineering の利用者に confidentiality ∈ {internal, public} を許すポリシー 1 件。
    private void SeedPolicyOnce()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        if (db.Policies.Any()) return;
        db.Policies.Add(AbacPolicy.Create(
            "grpc-ref-read",
            PolicyAction.Read,
            new Dictionary<string, List<string>> { ["department"] = ["engineering"] },
            new Dictionary<string, List<string>> { ["confidentiality"] = ["internal", "public"] }));
        db.SaveChanges();
    }

    private static ResolveScopeRequest EngineeringRequest(string action = "") =>
        new()
        {
            UserId = "alice",
            Action = action,
            UserAttributes = { ["department"] = "engineering", ["clearance"] = "internal" },
        };

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private AuthzScope.AuthzScopeClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    // ── FR-05, NFR-09, 計画 ADR-0088 決定 1, [[IADR-0413]] (#1333) ──
    // 🔴 **gRPC 面でも本文の `user_attributes` は評価に用いられない。**
    // REST 面（`ClaimedUserAttributesAreIgnoredTests`）と**同じ 1 つの点**を通ることの確認である。

    // 🔴 T-1333-a: 偽の属性を主張しても通らない。
    [Fact]
    public async Task Claimed_user_attributes_do_not_grant_anything_over_grpc()
    {
        var token = GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);
        var liar = $"liar-{Guid.NewGuid():N}"[..20];
        // IdP 側は sales。**ポリシーが要求するのは engineering である。**
        _factory.Identity.Attributes[liar] = new Dictionary<string, string> { ["department"] = "sales" };

        var request = new ResolveScopeRequest
        {
            UserId = liar,
            UserAttributes = { ["department"] = "engineering" },
        };
        var resp = await PlainClient().ResolveAsync(
            request, headers: Bearer(token), cancellationToken: TestContext.Current.CancellationToken);

        resp.Granted.Should().BeFalse(
            "主張が評価に使われていたら engineering のポリシーにマッチしてしまう");
        _factory.Identity.LookedUp.Should().Contain(liar, "引き直しが実際に走っている");
    }

    // 🔴 T-1333-b: 「居ない」は**応答**である（`granted=false`。status にしない）。
    [Fact]
    public async Task An_unknown_user_is_denied_by_a_normal_response_over_grpc()
    {
        var token = GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);
        var ghost = $"ghost-{Guid.NewGuid():N}"[..20];
        _factory.Identity.Unknown.Add(ghost);

        var resp = await PlainClient().ResolveAsync(
            new ResolveScopeRequest { UserId = ghost, UserAttributes = { ["department"] = "engineering" } },
            headers: Bearer(token), cancellationToken: TestContext.Current.CancellationToken);

        resp.Granted.Should().BeFalse();
    }

    // 🔴 T-1333-c: 「引けなかった」は **status**（`UNAVAILABLE`）である。
    // 呼び出し元は `RpcException` を deny へ縮退するので **fail-closed は保たれる**が、
    // **後段の停止を「権限が無い」として記録しない**。
    [Fact]
    public async Task An_identity_provider_outage_is_unavailable_not_a_denial()
    {
        var token = GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);
        _factory.Identity.Failure = new HttpRequestException("Keycloak へ届かない");
        try
        {
            var act = async () => await PlainClient().ResolveAsync(
                EngineeringRequest(), headers: Bearer(token),
                cancellationToken: TestContext.Current.CancellationToken);

            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
                .Should().Be(StatusCode.Unavailable);
        }
        finally
        {
            _factory.Identity.Failure = null;
        }
    }

    // 呼び出し側の共通部品（CreatePlatformChannel = 平文 h2c ＋ s2s CallCredentials）を実際に通す。
    private sealed class FixedTokenProvider(string token) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct) => ValueTask.FromResult(token);
    }

    // T-01: 陽性対照。s2s トークン（platform-service）を CallCredentials で付けた h2c チャネルで往復し、
    // ポリシーに一致する利用者が granted=true とフィルタを得る。
    [Fact]
    public async Task Resolve_over_h2c_with_service_token_returns_granted_scope()
    {
        var token = GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);
        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            _factory.GrpcAddress, new FixedTokenProvider(token));
        var client = new AuthzScope.AuthzScopeClient(channel);

        var resp = await client.ResolveAsync(
            EngineeringRequest(), cancellationToken: TestContext.Current.CancellationToken);

        resp.Granted.Should().BeTrue();
        resp.UserId.Should().Be("alice");
        resp.AllowedFilters.Should().ContainSingle(f => f.Key == "confidentiality")
            .Which.AllowedValues.Should().BeEquivalentTo(["internal", "public"]);
        resp.Branches.Should().ContainSingle().Which.Name.Should().NotBeNullOrEmpty();
    }

    // T-02: gRPC 用ポートは HTTP/2 専用である。HTTP/1.1 の要求は受け付けない（Http1AndHttp2 ではない）。
    // 陽性対照: 同じ要求が HTTP/1.1 のポートでは 200 を返す（＝ gRPC を有効にしても 8080 相当は残る）。
    [Fact]
    public async Task Grpc_port_rejects_http11_while_http_port_still_serves()
    {
        using var http11 = new HttpClient
        {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        // Kestrel は Http2 専用エンドポイントへの HTTP/1.1 要求に 400 Bad Request を返す
        // （接続を黙って切るのではない。実測 2026-09-05）。要求が **処理されない** ことが要点である。
        var onGrpcPort = await http11.GetAsync(
            $"{_factory.GrpcAddress}/health/live", TestContext.Current.CancellationToken);
        onGrpcPort.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "h2c 専用ポートは HTTP/1.1 の要求を処理しない（Http1AndHttp2 ではない）");

        var onHttpPort = await http11.GetAsync(
            $"{_factory.HttpAddress}/health/live", TestContext.Current.CancellationToken);
        onHttpPort.StatusCode.Should().Be(HttpStatusCode.OK,
            "gRPC リスナを足しても HTTP/1.1 のポート（REST・/health/*）は消えない");
    }

    // T-03: 陰性対照。資格情報が無ければ UNAUTHENTICATED。
    [Fact]
    public async Task Resolve_without_credentials_is_unauthenticated()
    {
        var act = async () => await PlainClient().ResolveAsync(
            EngineeringRequest(), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.Unauthenticated);
    }

    // T-04: 陰性対照。**利用者のトークン（管理者であっても）を転送しても通らない** —— s2s の面は
    // platform-service ロールだけを通す。これが緩むと利用者トークンの転送（confused deputy）が成立する。
    [Fact]
    public async Task Resolve_with_forwarded_user_token_is_permission_denied()
    {
        var userToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);

        var act = async () => await PlainClient().ResolveAsync(
            EngineeringRequest(), headers: Bearer(userToken), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.PermissionDenied);
    }

    // T-05: deny-by-default。s2s は正しくても、該当ポリシーの無い利用者は granted=false（エラーではなく応答）。
    [Fact]
    public async Task Resolve_for_user_without_matching_policy_is_not_granted()
    {
        var token = GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);
        var request = new ResolveScopeRequest
        {
            UserId = "bob",
            UserAttributes = { ["department"] = "sales" },
        };

        var resp = await PlainClient().ResolveAsync(
            request, headers: Bearer(token), cancellationToken: TestContext.Current.CancellationToken);

        resp.Granted.Should().BeFalse();
        resp.AllowedFilters.Should().BeEmpty();
        resp.Branches.Should().BeEmpty();
    }

    // T-06: 不正な action は INVALID_ARGUMENT（REST の 400 と同値。黙って空スコープへ写さない）。
    [Fact]
    public async Task Resolve_with_invalid_action_is_invalid_argument()
    {
        var token = GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);

        var act = async () => await PlainClient().ResolveAsync(
            EngineeringRequest(action: "delete"), headers: Bearer(token),
            cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.InvalidArgument);
    }

    // T-07: REST と gRPC は同じ評価器を呼ぶ —— 同じ入力に対して granted / filters / branches が一致する。
    // （REST が同じプロセスの HTTP/1.1 ポートで応えることが、ポート維持の 2 つ目の証明でもある。）
    [Fact]
    public async Task Rest_and_grpc_resolve_the_same_scope()
    {
        var token = GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);
        var grpc = await PlainClient().ResolveAsync(
            EngineeringRequest(), headers: Bearer(token), cancellationToken: TestContext.Current.CancellationToken);

        // 🔴 **REST 面も `ServiceCaller` を要る**（計画 ADR-0088 決定 2 / #1333）——
        // 従前この端点は認可を 1 つも掛けていなかった。gRPC と**同じ 1 つのポリシー**である。
        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var restResp = await http.PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest("alice", new Dictionary<string, string>
            {
                ["department"] = "engineering",
                ["clearance"] = "internal",
            }),
            TestContext.Current.CancellationToken);
        restResp.EnsureSuccessStatusCode();
        var rest = (await restResp.Content.ReadFromJsonAsync<AccessScopeResponse>(
            TestContext.Current.CancellationToken))!;

        grpc.Granted.Should().Be(rest.Granted);
        grpc.UserId.Should().Be(rest.UserId);
        grpc.AllowedFilters.Select(f => (f.Key, Values: f.AllowedValues.ToList()))
            .Should().BeEquivalentTo(rest.AllowedFilters.Select(f => (f.Key, Values: f.AllowedValues)));
        grpc.Branches.Select(b => b.Name).Should().BeEquivalentTo(rest.Branches!.Select(b => b.Name));
    }

    // T-12: 構造の門。gRPC サービス型が ServiceCaller ポリシーを宣言していること（属性が外れると
    // T-03 / T-04 が落ちるが、どの層で外れたかを名指しするためにここでも固定する）。
    [Fact]
    public void Grpc_service_declares_service_caller_policy()
    {
        var attr = typeof(AuthzScopeGrpcService).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }
}
