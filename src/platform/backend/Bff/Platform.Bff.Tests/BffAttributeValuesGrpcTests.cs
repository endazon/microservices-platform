using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Bff.Endpoints.Search;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Authz;
using System.Net;
using System.Net.Http.Json;
using Pb = Knowledge.Contracts.Grpc.Retrieval.V1;

namespace Platform.Bff.Tests;

// FR-04, FR-05, NFR-09, NFR-16, SC-01, SC-08, ADR-0004, ADR-0029, ADR-0043, ADR-0075,
// 計画 ADR-0086 決定 1, [[IADR-0151]], [[IADR-0253]], [[IADR-0379]], [[IADR-0401]],
// [[IADR-0402]], [[IADR-0410]], [[IADR-0416]], [[IADR-0417]] (#1255):
// `/bff/attribute-values` を east-west gRPC へ振り替える切替と、その縮退を固定する。
//
// 🔴 **既定は REST（正）である。** 切替は `Services:RetrievalServiceGrpc` の有無だけで決まり、
// 未設定なら gRPC クライアントは DI に 1 つも入らない（戻すのは構成を外すだけ）。
public class BffAttributeValuesGrpcTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffAttributeValuesGrpcTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        _factory.ScopeFilters = [];
        _factory.ScopeBranches = null;
        _factory.AttributeValuesStatusCode = HttpStatusCode.OK;
        _factory.StubAttributeValues = ["社内", "規程"];
    }

    // ── 登録の門（構成でしか切り替わらないこと） ─────────────────────────────────

    // 🔴 未設定なら**何も登録しない**。REST が正であることの実体はこの 1 行である。
    [Fact]
    public void Registration_is_a_no_op_without_the_grpc_address()
    {
        var services = new ServiceCollection();
        services.AddAttributeValuesGrpcClient(new ConfigurationBuilder().Build());

        services.Should().NotContain(d => d.ServiceType == typeof(AttributeValuesGrpcClient));
    }

    // 陽性対照: アドレスが在れば登録される（上だけだと「常に何もしない」実装でも緑になる）。
    [Fact]
    public void Registration_adds_the_client_when_the_address_is_configured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAttributeValuesGrpcClient(
            Config(AttributeValuesGrpcClient.AddressKey, "http://retrieval-service:8081"));

        services.Should().Contain(d => d.ServiceType == typeof(AttributeValuesGrpcClient));
    }

    // 🔴 **BFF は 3 つ目の宛先を持つ。** 認可サービス宛のチャネルは `AddAuthzScopeGrpcClient` が
    // **キー無し**で登録し、文書サービス宛は `DocumentServiceGrpc` のキーを持つ。
    // 検索宛を同じキー無しで登録すると、属性値のクライアントが認可サービスへ繋がる（あるいは逆）。
    [Fact]
    public void Retrieval_channel_is_keyed_and_does_not_collide_with_the_other_destinations()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AuthzScopeGrpcClient.AddressKey] = "http://authorization-service:8081",
            [AttributeValuesGrpcClient.AddressKey] = "http://retrieval-service:8081",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthzScopeGrpcClient(config);
        services.AddAttributeValuesGrpcClient(config);

        services.Count(d => d.ServiceType == typeof(GrpcChannel) && d.ServiceKey is null)
            .Should().Be(1, "キー無しのチャネルは認可サービス宛の 1 本だけである");
        services.Should().Contain(d => d.ServiceType == typeof(GrpcChannel)
            && Equals(d.ServiceKey, AttributeValuesGrpcClient.ChannelKey));
    }

    // 🔴 [[IADR-0412]] 決定 5 / [[IADR-0402]] 決定 6: **同じ宛先へ 2 本目のチャネルを張らせない。**
    // 二重に登録しても 1 本のままであること（`GetRequiredKeyedService` は最後の登録を返すので、
    // 二重登録は障害としては現れず、宛先ごと 1 本という決定だけが静かに破れる）。
    [Fact]
    public void Registering_twice_still_yields_a_single_channel_for_the_destination()
    {
        var config = Config(AttributeValuesGrpcClient.AddressKey, "http://retrieval-service:8081");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAttributeValuesGrpcClient(config);
        services.AddAttributeValuesGrpcClient(config);

        services.Count(d => d.ServiceType == typeof(GrpcChannel)
            && Equals(d.ServiceKey, AttributeValuesGrpcClient.ChannelKey))
            .Should().Be(1);
    }

    // ── 面が運ぶもの（決定 2・3） ───────────────────────────────────────────────

    // 🔴 **呼び出し元が解決したスコープを運ぶ口が存在しない**（[[IADR-0410]] / [[IADR-0417]] 決定 2）。
    // 契約側（proto）でも固定しているが、**呼び出し側にも門を置く** ——
    // 口が生えたときに気づく場所は、面と呼び出し元の両方に要る。
    [Fact]
    public void The_request_carries_a_user_context_and_never_a_resolved_scope()
    {
        Pb.ListValuesRequest.Descriptor.Fields.InDeclarationOrder().Select(f => f.Name)
            .Should().BeEquivalentTo(["key", "user", "narrow_to"]);
    }

    // 🔴 決定 3: **BFF は `narrow_to` を使わない。** 解決済みスコープを写すと分岐を平たい集合へ潰し、
    // [[IADR-0253]] 決定 2 の非包含により**分岐単独で到達できる文書の値が候補から落ちる**。
    [Fact]
    public async Task The_bff_sends_no_narrowing_even_when_the_resolved_scope_has_branches()
    {
        _factory.ScopeBranches =
        [
            new Platform.Shared.Contracts.Dtos.AccessScopeBranch(
                "sales", [new Platform.Shared.Contracts.Dtos.AttributeFilter("dept", ["sales"])]),
            new Platform.Shared.Contracts.Dtos.AccessScopeBranch(
                "cleared", [new Platform.Shared.Contracts.Dtos.AttributeFilter("clearance", ["secret"])]),
        ];
        var stub = new StubAttributeValuesInvoker();
        var client = GrpcClient(stub);
        // 🔴 **属性を持つ主体で測る。** 既定の主体は属性クレームを 1 つも持たないので、
        // 「何かを絞り込みへ写す」変異が**空のまま**通ってしまう（実測で生存した）。
        client.DefaultRequestHeaders.Add(TestAuthHandler.AttributesHeader, "clearance=secret;department=sales");

        await client.PostAsJsonAsync("/bff/attribute-values", new { key = "tags" },
            TestContext.Current.CancellationToken);

        stub.LastRequest.Should().NotBeNull("陽性対照: gRPC 経路が実際に呼ばれている");
        stub.LastRequest!.User.UserAttributes.Should().NotBeEmpty(
            "★ 陽性対照 —— 主体は実際に属性を持っている（空どうしの一致ではない）");
        stub.LastRequest.NarrowTo.Should().BeEmpty(
            "解決済みスコープを絞り込みへ写すと分岐がキー単位 union へ潰れる（IADR-0253 決定 2）");
    }

    // 🔴 **利用者文脈は本文で運ぶ**（`ADR-0086` 決定 1 / [[IADR-0411]]）。
    // action は既定へ丸めず `read` を明示する（[[IADR-0272]] 決定 4）。
    [Fact]
    public async Task The_bff_carries_the_user_context_in_the_body()
    {
        var stub = new StubAttributeValuesInvoker();
        var client = GrpcClient(stub);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");
        client.DefaultRequestHeaders.Add(TestAuthHandler.AttributesHeader, "clearance=secret;department=sales");

        await client.PostAsJsonAsync("/bff/attribute-values", new { key = "tags" },
            TestContext.Current.CancellationToken);

        stub.LastRequest!.User.Should().NotBeNull();
        stub.LastRequest.User.UserId.Should().NotBeNullOrEmpty("利用者が分からない要求は送らない");
        stub.LastRequest.User.Action.Should().Be("read", "既定へ丸めず明示する");
        stub.LastRequest.Key.Should().Be("tags");

        // 🔴 **属性は判定の入力である**（`ADR-0086` 決定 1）。運ばないと後段の ABAC が別の答えを出す。
        // 抽出は共有点（`BffScopeResolver.ExtractUserAttributes`）1 つに集約してある（[[IADR-0411]]）ので、
        // **ここでキーを列挙して数えない** —— 共有点が読むキーが増えたときに壊れる試験を作らない。
        stub.LastRequest.User.UserAttributes.Should().Contain(
            new KeyValuePair<string, string>("clearance", "secret"));
        stub.LastRequest.User.UserAttributes.Should().Contain(
            new KeyValuePair<string, string>("department", "sales"));

        // 🔴 **ロールは属性へ混ぜない**（[[IADR-0411]] / #1326）。realm ロールは ABAC の軸ではない。
        stub.LastRequest.User.UserAttributes.Should().NotContainKey("roles");
    }

    // ── 端点の振り替え（REST と gRPC が同じ答えを返すこと） ─────────────────────────

    // 既定（gRPC 未登録）では後段 HTTP スタブが呼ばれる。gRPC を登録すると**呼ばれない**。
    // 「gRPC が答えた」ことは、HTTP スタブが 500 を返す設定にしても 200 が返ることで示す。
    [Fact]
    public async Task Values_use_grpc_when_registered_and_do_not_touch_the_rest_stub()
    {
        _factory.AttributeValuesStatusCode = HttpStatusCode.InternalServerError;

        // 陰性対照: gRPC が無ければ後段の 500 がそのまま透過する。
        var viaRest = await _factory.CreateClient().PostAsJsonAsync("/bff/attribute-values",
            new { key = "tags" }, TestContext.Current.CancellationToken);
        viaRest.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        // 陽性: gRPC が在れば REST を触らずに 200。
        var stub = new StubAttributeValuesInvoker { Values = ["社内", "規程"] };
        var resp = await GrpcClient(stub).PostAsJsonAsync("/bff/attribute-values",
            new { key = "tags" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(
            TestContext.Current.CancellationToken);
        body!.Values.Should().Equal(["社内", "規程"]);
        stub.Calls.Should().Be(1);
    }

    // FR-09, SC-05, SC-09, [[IADR-0152]] 決定 3: 🔴 **辞書は BFF が添える。**
    // 面には辞書が無い（[[IADR-0417]] 決定 7）ので、輸送を替えても管理者の応答形は変わらない。
    [Fact]
    public async Task The_dictionary_is_still_added_by_the_bff_over_grpc()
    {
        _factory.TagDictionaryFetched = false;
        var client = GrpcClient(new StubAttributeValuesInvoker());
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-admin");

        var resp = await client.PostAsJsonAsync("/bff/attribute-values", new { key = "tags" },
            TestContext.Current.CancellationToken);

        var body = (await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(
            TestContext.Current.CancellationToken))!;
        body.Dictionary.Should().NotBeNull("辞書は後段ではなく BFF が添える");
        _factory.TagDictionaryFetched.Should().BeTrue();
    }

    // 🔴 一般利用者には辞書が出ないことも輸送に依らない（ADR-0043 決定 1）。
    [Fact]
    public async Task A_general_user_still_gets_no_dictionary_over_grpc()
    {
        var client = GrpcClient(new StubAttributeValuesInvoker());
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");

        var resp = await client.PostAsJsonAsync("/bff/attribute-values", new { key = "tags" },
            TestContext.Current.CancellationToken);

        (await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(
            TestContext.Current.CancellationToken))!.Dictionary.Should().BeNull();
    }

    // ── 縮退（REST と同じ枝を通る） ─────────────────────────────────────────────

    // 🔴 引けなかったら**空配列**（現行 REST の catch と同じ枝を共有する）——
    // [[IADR-0151]] 決定 5 の存在秘匿はそのままである。
    [Fact]
    public async Task Values_over_grpc_degrade_to_an_empty_list_when_the_call_fails()
    {
        var stub = new StubAttributeValuesInvoker
        {
            Failure = new RpcException(new Status(StatusCode.Unavailable, "stub")),
        };

        var resp = await GrpcClient(stub).PostAsJsonAsync("/bff/attribute-values",
            new { key = "tags" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(
            TestContext.Current.CancellationToken))!.Values.Should().BeEmpty();
    }

    // 🔴 [[IADR-0417]] 決定 9: **後段が答えた上での失敗を 200 空応答で隠さない。**
    // REST は非 2xx を透過しており、gRPC で全 status を「不達」へ畳むと
    // **その枝が静かに消える**（運用側が後段の不調に気づけなくなる）。
    // 🔴 上の `Unavailable` の試験と**対**である —— 片方だけでは「常に空」も「常に 502」も通る。
    [Theory]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.InvalidArgument)]
    public async Task A_failure_answered_by_the_downstream_is_not_hidden_as_an_empty_list(StatusCode status)
    {
        var stub = new StubAttributeValuesInvoker
        {
            Failure = new RpcException(new Status(status, "stub")),
        };

        var resp = await GrpcClient(stub).PostAsJsonAsync("/bff/attribute-values",
            new { key = "tags" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            "REST が非 2xx を透過している枝を、輸送の差し替えのついでに潰さない");
    }

    // 🔴 s2s トークンが取れないとき（構成不備・IdP 不達）も同じ枝へ落ちる。
    // 匿名で呼び直したりしない（載せるのは s2s トークンだけである）。
    [Fact]
    public async Task Values_over_grpc_degrade_when_the_service_token_cannot_be_obtained()
    {
        var stub = new StubAttributeValuesInvoker
        {
            Failure = new InvalidOperationException("ServiceToken:ClientId / ClientSecret が未設定"),
        };

        var resp = await GrpcClient(stub).PostAsJsonAsync("/bff/attribute-values",
            new { key = "tags" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(
            TestContext.Current.CancellationToken))!.Values.Should().BeEmpty();
    }

    // 🔴 **サーバ側の解決が不許可なら後段を呼ばない**（輸送を替えても早期の門は残る）。
    [Fact]
    public async Task A_denied_scope_still_short_circuits_before_the_grpc_call()
    {
        _factory.SearchScopeGranted = false;
        var stub = new StubAttributeValuesInvoker();

        var resp = await GrpcClient(stub).PostAsJsonAsync("/bff/attribute-values",
            new { key = "tags" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.Calls.Should().Be(0);
    }

    private static IConfiguration Config(string key, string value) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = value }).Build();

    // gRPC クライアントを DI へ差し込んだテスト用ホスト。**構成キーではなく DI へ直接入れる** ——
    // 実チャネルを張らずに `SearchBffEndpoints` の経路選択（登録の有無）だけを測るためである。
    private HttpClient GrpcClient(StubAttributeValuesInvoker stub) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton(new AttributeValuesGrpcClient(
                new Pb.AttributeValues.AttributeValuesClient(stub)))))
            .CreateClient();

    // 生成クライアントは CallInvoker の上に乗る。**要求を記録する** ——
    // 面が何を運んだか（scope を運んでいないこと・利用者文脈を運んでいること）を観測できる。
    private sealed class StubAttributeValuesInvoker : CallInvoker
    {
        public int Calls { get; private set; }
        public Pb.ListValuesRequest? LastRequest { get; private set; }

        /// <summary>値の応答（既定は器の HTTP スタブと同じ 2 件）。</summary>
        public List<string> Values { get; init; } = ["社内", "規程"];

        /// <summary>rpc を失敗させる（縮退の検証用）。</summary>
        public Exception? Failure { get; init; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Calls++;
            LastRequest = request as Pb.ListValuesRequest;
            if (Failure is not null) return Failed<TResponse>(Failure);

            var resp = new Pb.ListValuesResponse();
            resp.Values.AddRange(Values);
            return Ok<TResponse>(resp);
        }

        private static AsyncUnaryCall<TResponse> Ok<TResponse>(object response) =>
            new(Task.FromResult((TResponse)response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });

        private static AsyncUnaryCall<TResponse> Failed<TResponse>(Exception ex) =>
            new(Task.FromException<TResponse>(ex), Task.FromResult(new Metadata()),
                () => ex is RpcException rpc ? rpc.Status : Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();
    }
}
