using System.Net.Http.Json;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc.Retrieval.V1;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.Search.AttributeValues;
using RetrievalService.Tests.Grpc;
using Pb = Knowledge.Contracts.Grpc.Retrieval.V1;

namespace RetrievalService.Tests.Features.Search;

// FR-04, FR-05, NFR-09, NFR-16, SC-01, SC-08, ADR-0004, ADR-0029, ADR-0043, ADR-0075,
// 計画 ADR-0086 決定 1, [[IADR-0151]], [[IADR-0379]], [[IADR-0401]], [[IADR-0410]],
// [[IADR-0415]], [[IADR-0416]], [[IADR-0417]] (#1255):
// 権限内属性値の照会の gRPC 面（`knowledge.retrieval.v1.AttributeValues`）を
// **実 Kestrel の h2c ポート**で往復し、REST との同値・s2s の要求・面に出さないものを固定する。
//
// 陽性対照（T-01 / T-04）と陰性対照（T-02 / T-03）を同じ器で対にする ——
// 「拒否された」だけでは器が壊れているのか認可が効いているのか区別できない。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcAttributeValuesTests
{
    private const string ServiceSubject = "service-account-bff";
    private readonly GrpcKestrelFactory _factory;

    public GrpcAttributeValuesTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private static string ServiceToken() =>
        GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);

    private Pb.AttributeValues.AttributeValuesClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static UserContext User(string id = "alice", string? dept = null)
    {
        var user = new UserContext { UserId = id, Action = "read" };
        if (dept is not null) user.UserAttributes["department"] = dept;
        return user;
    }

    private async Task SeedAsync(params (string Text, string Dept)[] chunks)
    {
        foreach (var (text, dept) in chunks)
            await _factory.Index.UpsertAsync(new ChunkPayload(
                Guid.NewGuid(), Guid.NewGuid(), $"doc:{text}", text,
                new float[1536], $"s3://b/{Guid.NewGuid()}.md",
                new Dictionary<string, string> { ["dept"] = dept }, []));
    }

    // T-01: 陽性対照。s2s トークンを CallCredentials で付けた h2c チャネルで往復し、値が返る。
    [Fact]
    public async Task ListValues_over_h2c_with_service_token_returns_reachable_values()
    {
        _factory.Authoritative = new AccessScopeResponse("alice", [], Granted: true);
        await SeedAsync(($"h2c-{Guid.NewGuid():N}", "sales"));

        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            _factory.GrpcAddress, new FixedTokenProvider(ServiceToken()));
        var client = new Pb.AttributeValues.AttributeValuesClient(channel);

        var resp = await client.ListValuesAsync(
            new ListValuesRequest { Key = "dept", User = User() },
            cancellationToken: TestContext.Current.CancellationToken);

        resp.Values.Should().Contain("sales");
    }

    // 🔴 T-05（本 issue の核心）: **呼び出し元が主張した scope を受ける口が存在しない。**
    // 面が運ぶのは利用者文脈だけであり、**呼び出し先が自分で解決する**（[[IADR-0410]] / [[IADR-0416]]）。
    [Fact]
    public void The_face_carries_a_user_context_and_never_a_resolved_scope()
    {
        var fields = ListValuesRequest.Descriptor.Fields.InDeclarationOrder()
            .Select(f => f.Name).ToList();

        fields.Should().BeEquivalentTo(["key", "user", "narrow_to"]);
        fields.Should().NotContain("scope",
            "呼び出し元が解決したスコープを受ける口を開くと、到達できる誰もが任意の scope を主張できる");

        // 🔴 応答も**値だけ**である（辞書・使用件数は面に無い。[[IADR-0401]] 決定 2）。
        ListValuesResponse.Descriptor.Fields.InDeclarationOrder()
            .Select(f => f.Name).Should().Equal(["values"]);
    }

    // 🔴 T-06: **呼び出し先が自分で判定している**ことの直接の観測。
    // 記録が無ければ「呼び出し元のスコープを信じている」実装でも上の試験は緑になり得る。
    [Fact]
    public async Task The_service_resolves_the_scope_from_the_user_context_in_the_body()
    {
        var user = $"probe-{Guid.NewGuid():N}"[..20];
        _factory.Authoritative = new AccessScopeResponse(user, [], Granted: true);

        await PlainClient().ListValuesAsync(
            new ListValuesRequest { Key = "dept", User = User(user, dept: "engineering") },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        var seen = _factory.ResolvedFor.Where(r => r.UserId == user).ToList();
        seen.Should().ContainSingle("本文の文脈で自分の判定を行っている");
        seen[0].Attributes.Should().Contain(
            new KeyValuePair<string, string>("department", "engineering"),
            "属性も本文で運ばれる（判定の入力であって結果ではない）");
    }

    // 🔴 T-07: **絞り込みは権限を広げない**（[[IADR-0415]] の narrowing-only）。
    [Fact]
    public async Task A_narrowing_cannot_reach_beyond_the_resolved_scope()
    {
        _factory.Authoritative = new AccessScopeResponse(
            "alice", [new AttributeFilter("dept", ["sales"])], Granted: true);
        await SeedAsync(("売上", "sales"), ("人事", "hr"));

        var request = new ListValuesRequest { Key = "dept", User = User() };
        request.NarrowTo.Add("dept", new NarrowTo { Values = { "hr" } });

        var resp = await PlainClient().ListValuesAsync(
            request, headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        resp.Values.Should().NotContain("hr", "絞り込みは権限の根拠ではない");
    }

    // ★ T-07 の**陽性対照**: 絞り込みは**実際に絞る**。
    // 🔴 これが無いと「`narrow_to` を読まずに捨てる」実装でも T-07 は緑になる ——
    // 権限を広げないことだけを測ると、**何もしない実装**が合格してしまう。
    [Fact]
    public async Task A_narrowing_actually_narrows_within_the_resolved_scope()
    {
        _factory.Authoritative = new AccessScopeResponse("alice", [], Granted: true);
        var keep = $"keep-{Guid.NewGuid():N}"[..12];
        var drop = $"drop-{Guid.NewGuid():N}"[..12];
        await SeedAsync(("残す", keep), ("落とす", drop));

        var request = new ListValuesRequest { Key = "dept", User = User() };
        request.NarrowTo.Add("dept", new NarrowTo { Values = { keep } });

        var resp = await PlainClient().ListValuesAsync(
            request, headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        resp.Values.Should().Contain(keep);
        resp.Values.Should().NotContain(drop, "指定した絞り込みが効いている");
    }

    // T-02: 陰性対照。資格情報が無ければ UNAUTHENTICATED。
    [Fact]
    public async Task ListValues_without_credentials_is_unauthenticated()
    {
        var act = async () => await PlainClient().ListValuesAsync(
            new ListValuesRequest { Key = "dept", User = User() },
            cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.Unauthenticated);
    }

    // 🔴 T-03: **管理者の利用者トークンでも PERMISSION_DENIED**（confused deputy の防止）。
    // REST の受け口は認可を持たないので、「認証さえあれば通る」形にすると
    // s2s の面が利用者トークンでも開く（[[IADR-0379]] 決定 4）。
    [Fact]
    public async Task ListValues_with_forwarded_admin_user_token_is_permission_denied()
    {
        var adminToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);

        var act = async () => await PlainClient().ListValuesAsync(
            new ListValuesRequest { Key = "dept", User = User() },
            headers: Bearer(adminToken), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.PermissionDenied);
    }

    // 🔴 T-08: **利用者が分からないのは要求の誤りである**（deny へ畳まない）。
    // 畳むと呼び出し元の配線誤りが「候補が 1 件も無い」と見分けられなくなる。
    [Fact]
    public async Task A_request_without_a_user_is_invalid_argument()
    {
        var act = async () => await PlainClient().ListValuesAsync(
            new ListValuesRequest { Key = "dept" },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.InvalidArgument);
    }

    // T-04 ＋ T-09: REST と gRPC が**同じ問い合わせ**（`AttributeValuesEndpoint.ListAsync`）を通る。
    //
    // 🔴 REST が同じプロセスの HTTP/1.1 ポートで応えること自体が、**h2c を有効にしても
    // 8080 側が消えていない**ことの証明でもある。
    [Fact]
    public async Task Rest_and_grpc_report_the_same_values()
    {
        _factory.Authoritative = new AccessScopeResponse("alice", [], Granted: true);
        var tag = $"same-{Guid.NewGuid():N}"[..12];
        await SeedAsync((tag, tag));

        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        // FR-05, NFR-09, ADR-0084, [[IADR-0418]] (#1318): 🔴 **REST 面も認証を要する。**
        // 本器は**本物の JwtBearer パイプライン**を通すので、利用者のトークンを実際に発行して載せる
        // （`ServiceCaller` ではない —— REST 面が要求するのは realm の認証済み主体だけである）。
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", $"Bearer {GrpcKestrelFactory.IssueToken("alice", [])}");
        var restResp = await http.PostAsJsonAsync("/search/attribute-values",
            new AttributeValuesRequest("dept", new AccessScope([], GrantsAccess: true)),
            TestContext.Current.CancellationToken);
        var rest = (await restResp.Content.ReadFromJsonAsync<AttributeValuesResponse>(
            TestContext.Current.CancellationToken))!;

        var grpc = await PlainClient().ListValuesAsync(
            new ListValuesRequest { Key = "dept", User = User() },
            headers: Bearer(ServiceToken()), cancellationToken: TestContext.Current.CancellationToken);

        grpc.Values.Should().BeEquivalentTo(rest.Values, "輸送を替えても応答の意味は変わらない");
        grpc.Values.Should().Contain(tag, "★ 陽性対照 —— 両方が空で一致したのではない");
    }

    // T-10: 構造の門。gRPC サービス型が ServiceCaller ポリシーを宣言していること。
    [Fact]
    public void Grpc_service_declares_service_caller_policy()
    {
        var attr = typeof(AttributeValuesGrpcService)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(PlatformAuthPolicies.ServiceCaller);
    }

    // s2s トークンの発行側を固定値へ差し替える（IdP を持たないため）。
    private sealed class FixedTokenProvider(string token) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct = default) => new(token);
    }
}
