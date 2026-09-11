using System.Net.Http.Json;
using AuthorizationService.Domain.Ports;
using AuthorizationService.Features.Users.Directory;
using AuthorizationService.Tests.Grpc;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Contracts.Grpc.Authz.V1;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace AuthorizationService.Tests.Features.Users.Directory;

// FR-05, FR-16, UC-04, UC-09, SC-06, SC-12, SC-17, NFR-09, NFR-16, ADR-0004, ADR-0029, ADR-0062,
// ADR-0064, ADR-0074, ADR-0075, [[IADR-0329]], [[IADR-0379]], [[IADR-0385]], [[IADR-0401]] (#1255):
// 利用者名簿の**狭い読み口**（`platform.authz.v1.UserDirectory`）を**実 Kestrel の h2c ポート**で
// 往復し、s2s トークンの検証と「居ない／引けなかった」の分離が gRPC 経路でも保たれることを固定する。
//
// 陽性対照（T-S-01）と陰性対照（T-S-02 / T-S-03）を同じ器で対にする —— 「拒否された」だけでは
// 器が壊れているのか認可が効いているのか区別できない。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcUserDirectoryTests
{
    private const string ServiceSubject = "service-account-datasource-service";
    private readonly GrpcKestrelFactory _factory;

    public GrpcUserDirectoryTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private static string ServiceToken() =>
        GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);

    private UserDirectory.UserDirectoryClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    // 偽の身元プロバイダ（InMemoryIdentityAdminClient）が持つ既定の 4 名。
    // 🔴 `takahashi.jiro` は **enabled=false**（退職者）である —— 実在するが有効ではない。
    private const string EnabledUser = "tanaka.taro";
    private const string DisabledUser = "takahashi.jiro";

    // T-S-01: 陽性対照。s2s トークン（platform-service）を CallCredentials で付けた h2c チャネルで
    // 往復し、実在する利用者名が exists=true として返る。
    [Fact]
    public async Task CheckUsernames_over_h2c_with_service_token_reports_existence()
    {
        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            _factory.GrpcAddress, new FixedTokenProvider(ServiceToken()));
        var client = new UserDirectory.UserDirectoryClient(channel);

        var request = new CheckUsernamesRequest { Usernames = { EnabledUser, "no-such-user" } };
        var resp = await client.CheckUsernamesAsync(
            request, cancellationToken: TestContext.Current.CancellationToken);

        resp.Results.Should().HaveCount(2);
        resp.Results[0].Username.Should().Be(EnabledUser);
        resp.Results[0].Exists.Should().BeTrue();
        resp.Results[1].Username.Should().Be("no-such-user");
        resp.Results[1].Exists.Should().BeFalse("「居ない」はエラーではなく応答である");
    }

    // T-S-09: `CheckUsernames` は要求と**同じ順・同じ数**を返す。
    // 呼び出し元は位置ではなく username で読むが、「送った名前がすべて答えに現れる」ことは
    // 結果を集合へ畳むときの前提である（落ちた名前が黙って「実在しない」に化けない）。
    [Fact]
    public async Task CheckUsernames_preserves_request_order_and_count()
    {
        var requested = new[] { "zeta", EnabledUser, "alpha", DisabledUser, EnabledUser };
        var request = new CheckUsernamesRequest();
        request.Usernames.AddRange(requested);

        var resp = await PlainClient().CheckUsernamesAsync(
            request, headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        resp.Results.Select(r => r.Username).Should().Equal(requested);
    }

    // T-P2-05（呼び出し先の側）: 🔴 **無効化された利用者も実在として数える。**
    // ADR-0074 決定 4 が課すのは「実在すること」であって「有効であること」ではない
    // （退職者が所有者だった文書は所有者を失わない）。
    [Fact]
    public async Task CheckUsernames_counts_disabled_user_as_existing()
    {
        // 陽性対照: 偽の身元プロバイダが実際に無効な利用者を持っていること。
        using (var scope = _factory.Services.CreateScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<IIdentityAdminClient>();
            var users = await identity.ListUsersAsync(TestContext.Current.CancellationToken);
            users.Should().Contain(u => u.Username == DisabledUser && !u.Enabled);
        }

        var resp = await PlainClient().CheckUsernamesAsync(
            new CheckUsernamesRequest { Usernames = { DisabledUser } },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        resp.Results.Should().ContainSingle().Which.Exists.Should().BeTrue();
    }

    // T-P2-06（呼び出し先の側）: 🔴 照合は**序数一致**である（DataSourceService の現行と同じ）。
    // 大小文字違いは「実在しない」。移行で照合規則を変えない。
    [Fact]
    public async Task CheckUsernames_matches_ordinally()
    {
        var resp = await PlainClient().CheckUsernamesAsync(
            new CheckUsernamesRequest { Usernames = { EnabledUser.ToUpperInvariant() } },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        resp.Results.Should().ContainSingle().Which.Exists.Should().BeFalse();
    }

    // T-S-01（属性側の陽性対照）: 名指しした 1 人の ABAC 属性が返る。
    // 🔴 **照合は大小文字無視**である（McpServer の現行 FindRegistrarAsync と同じ）。
    [Fact]
    public async Task GetUserAttributes_returns_attributes_and_matches_case_insensitively()
    {
        var resp = await PlainClient().GetUserAttributesAsync(
            new GetUserAttributesRequest { Username = EnabledUser.ToUpperInvariant() },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        resp.Found.Should().BeTrue();
        resp.Username.Should().Be(EnabledUser, "名簿側の正準表記を返す");
        resp.Attributes.Should().ContainKey("department");
        resp.Attributes.Should().ContainKey("clearance");
    }

    // 「居ない」は**応答**である（エラーではない）。呼び出し元がこれを「引けなかった」へ倒すかは
    // 呼び出し元の判断であり、輸送の側では決めない。
    [Fact]
    public async Task GetUserAttributes_for_unknown_user_is_not_found_response()
    {
        var resp = await PlainClient().GetUserAttributesAsync(
            new GetUserAttributesRequest { Username = "no-such-user" },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        resp.Found.Should().BeFalse();
        resp.Attributes.Should().BeEmpty();
    }

    // T-S-02: 陰性対照。資格情報が無ければ UNAUTHENTICATED（両 rpc とも）。
    [Fact]
    public async Task Directory_without_credentials_is_unauthenticated()
    {
        var check = async () => await PlainClient().CheckUsernamesAsync(
            new CheckUsernamesRequest { Usernames = { EnabledUser } },
            cancellationToken: TestContext.Current.CancellationToken);
        (await check.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.Unauthenticated);

        var attrs = async () => await PlainClient().GetUserAttributesAsync(
            new GetUserAttributesRequest { Username = EnabledUser },
            cancellationToken: TestContext.Current.CancellationToken);
        (await attrs.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.Unauthenticated);
    }

    // 🔴 T-S-03: **決定 2 を機械で守る唯一の点。**
    //
    // `GET /authz/users`（REST）は AdminOnly であり、従来 DataSourceService / McpServer は
    // **利用者の Authorization を転送**して通していた。s2s の面は `platform-service` だけを通し、
    // **管理者の利用者トークンでも PERMISSION_DENIED** にする —— ここが緩むと
    // 利用者トークンの転送（confused deputy）が成立し、決定 2（読み口を狭めて置き換える）の
    // 前提そのものが崩れる。
    [Fact]
    public async Task Directory_with_forwarded_admin_user_token_is_permission_denied()
    {
        var adminToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);

        var check = async () => await PlainClient().CheckUsernamesAsync(
            new CheckUsernamesRequest { Usernames = { EnabledUser } },
            headers: Bearer(adminToken), cancellationToken: TestContext.Current.CancellationToken);
        (await check.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.PermissionDenied);

        var attrs = async () => await PlainClient().GetUserAttributesAsync(
            new GetUserAttributesRequest { Username = EnabledUser },
            headers: Bearer(adminToken), cancellationToken: TestContext.Current.CancellationToken);
        (await attrs.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.PermissionDenied);
    }

    // T-S-04: REST と gRPC は同じ後段（IIdentityAdminClient）を読む —— 同じ入力に対して
    // 「実在する利用者名の集合」と「1 人の属性」が一致する。
    // （REST が同じプロセスの HTTP/1.1 ポートで応えることが、ポート維持の証明でもある。）
    //
    // 🔴 REST の `/authz/users` は **AdminOnly** なので、ここでは管理者の利用者トークンで叩く ——
    // **同じトークンで gRPC を叩くと上の T-S-03 が示すとおり PERMISSION_DENIED になる。**
    // 面ごとに通る資格情報が違うことが、そのまま「転送していない」ことの現れである。
    [Fact]
    public async Task Rest_and_grpc_report_the_same_directory()
    {
        var adminToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);
        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", adminToken);

        var restResp = await http.GetAsync("/authz/users", TestContext.Current.CancellationToken);
        restResp.EnsureSuccessStatusCode();
        var rest = (await restResp.Content.ReadFromJsonAsync<List<PlatformUserDto>>(
            TestContext.Current.CancellationToken))!;
        rest.Should().NotBeEmpty("陽性対照: REST 側が名簿を返していること");

        var names = rest.Select(u => u.Username).ToArray();
        var request = new CheckUsernamesRequest();
        request.Usernames.AddRange(names);
        var grpcCheck = await PlainClient().CheckUsernamesAsync(
            request, headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);
        grpcCheck.Results.Should().OnlyContain(r => r.Exists);

        var target = rest[0];
        var grpcAttrs = await PlainClient().GetUserAttributesAsync(
            new GetUserAttributesRequest { Username = target.Username },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);
        grpcAttrs.Found.Should().BeTrue();
        grpcAttrs.Username.Should().Be(target.Username);
        grpcAttrs.Attributes.Should().BeEquivalentTo(target.Attributes);
    }

    // T-P2-09（呼び出し先の側）: 🔴 集合値属性はカンマ連結のまま往復する（[[IADR-0385]] 決定 2）。
    // `map<string,string>` を `repeated` へ変えてはならない —— 同 IADR がその案を退けている。
    [Fact]
    public async Task GetUserAttributes_round_trips_set_valued_tags_as_comma_joined()
    {
        string userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<IIdentityAdminClient>();
            var users = await identity.ListUsersAsync(TestContext.Current.CancellationToken);
            var target = users.First(u => u.Username == EnabledUser);
            userId = target.Id;
            var attrs = new Dictionary<string, string>(target.Attributes) { ["tags"] = "sales,hr" };
            await identity.ReplaceAttributesAsync(userId, attrs, TestContext.Current.CancellationToken);
        }

        var resp = await PlainClient().GetUserAttributesAsync(
            new GetUserAttributesRequest { Username = EnabledUser },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        resp.Attributes["tags"].Should().Be("sales,hr");
    }

    // ── #1409 / [[IADR-0431]]: 退職の窓の判定を面へ載せた分 ──────────────────

    // FR-19, ADR-0096 決定 1, [[IADR-0428]] 決定 3, [[IADR-0431]] (#1409):
    // 🔴 **陰性対照。無効化されているだけでは削除の条件を満たさない。**
    // 起点（予約属性）が未供給なら `NOT_EVALUABLE` である ——
    // **人事連携が未配備の間はこれが通常の状態**であり、ここが `ELAPSED` へ倒れると
    // 全退職者の個人資料がいきなり消える。
    [Fact]
    public async Task GetUserAttributes_reports_not_evaluable_when_anchor_is_absent()
    {
        var resp = await PlainClient().GetUserAttributesAsync(
            new GetUserAttributesRequest { Username = DisabledUser },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        resp.Found.Should().BeTrue();
        resp.Enabled.Should().BeFalse("`takahashi.jiro` は無効化済みである");
        resp.RetentionEligibility.Should().Be(RetentionEligibility.NotEvaluable,
            "起点が未供給なら数えない（削除の対象にしない）");
    }

    // FR-19, ADR-0096 決定 1 (#1409): 在籍中の利用者は `enabled=true` で運ばれる。
    // 消費側の述語は `!Enabled` を含むので、ここが常に false になる変異は削除を全面的に止め、
    // 常に true になる変異は在籍者の資料を消す。**両方向の意味があるので陽性側も固定する。**
    [Fact]
    public async Task GetUserAttributes_reports_enabled_for_active_user()
    {
        var resp = await PlainClient().GetUserAttributesAsync(
            new GetUserAttributesRequest { Username = EnabledUser },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        resp.Enabled.Should().BeTrue();
        resp.RetentionEligibility.Should().Be(RetentionEligibility.NotEvaluable);
    }

    // FR-19, ADR-0036 D-09, ADR-0096 決定 1, [[IADR-0428]] 決定 2 (#1409):
    // 陽性と陰性を**同じ器で対にする**。30 日は構成に出していないので、
    // 動かすのは起点だけである（31 日前 → `ELAPSED` ／ 3 日前 → `WITHIN_WINDOW`）。
    [Theory]
    [InlineData(31, RetentionEligibility.Elapsed)]
    [InlineData(3, RetentionEligibility.WithinWindow)]
    public async Task GetUserAttributes_evaluates_the_thirty_day_window_from_the_anchor(
        int daysAgo, RetentionEligibility expected)
    {
        var key = AuthorizationService.Domain.RetentionAnchorAttributes.AccountDisabledAtKey;
        string userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<IIdentityAdminClient>();
            var users = await identity.ListUsersAsync(TestContext.Current.CancellationToken);
            userId = users.First(u => u.Username == DisabledUser).Id;
            await identity.SetRetentionAnchorAsync(userId, key,
                DateTimeOffset.UtcNow.AddDays(-daysAgo), TestContext.Current.CancellationToken);
        }

        try
        {
            var resp = await PlainClient().GetUserAttributesAsync(
                new GetUserAttributesRequest { Username = DisabledUser },
                headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

            resp.Enabled.Should().BeFalse();
            resp.RetentionEligibility.Should().Be(expected);
        }
        finally
        {
            // 器は collection 共有である。起点を残すと他の試験の前提が動く。
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IIdentityAdminClient>()
                .SetRetentionAnchorAsync(userId, key, null, TestContext.Current.CancellationToken);
        }
    }

    // 🔴 **契約の門。** proto3 の既定値 0 は `UNSPECIFIED` でなければならない ——
    // `ELAPSED` を 0 に置くと、本項目を知らない古い配備の応答が**そのまま削除の合図**になる
    // （[[IADR-0431]]。消費側の `_ => NotEvaluable` と対で効く）。
    [Fact]
    public void Retention_eligibility_zero_value_is_unspecified()
        => ((int)RetentionEligibility.Unspecified).Should().Be(0);

    // T-S-08: 構造の門。gRPC サービス型が ServiceCaller ポリシーを宣言していること
    // （属性が外れると T-S-02 / T-S-03 が落ちるが、どの層で外れたかを名指しするためにここでも固定する）。
    [Fact]
    public void Grpc_service_declares_service_caller_policy()
    {
        var attr = typeof(UserDirectoryGrpcService)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }

    // 呼び出し側の共通部品（CreatePlatformChannel = 平文 h2c ＋ s2s CallCredentials）を実際に通す。
    private sealed class FixedTokenProvider(string token) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct) => ValueTask.FromResult(token);
    }
}
