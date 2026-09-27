using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Bff.Endpoints.Documents;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using System.Net;
using System.Net.Http.Json;
using Pb = Knowledge.Contracts.Grpc.Document.V1;

namespace Platform.Bff.Tests;

// FR-05, FR-06, UC-03, SC-03, SC-05, NFR-09, NFR-16, ADR-0029, ADR-0056, ADR-0075,
// [[IADR-0009]], [[IADR-0041]], [[IADR-0045]], [[IADR-0379]], [[IADR-0402]] (#1255):
// BFF の文書読み取り 4 箇所を east-west gRPC へ振り替える切替と、その縮退を固定する。
//
// 🔴 **既定は REST（正）である。** 切替は `Services:DocumentServiceGrpc` の有無だけで決まり、
// 未設定なら gRPC クライアントは DI に 1 つも入らない（戻すのは構成を外すだけ）。
public class BffDocumentGrpcTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffDocumentGrpcTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        _factory.ScopeFilters = [];
        _factory.ScopeBranches = null;
        _factory.DocumentStatusCode = HttpStatusCode.OK;
    }

    private static string DetailPath => $"/bff/documents/{BffTestFactory.StubDocumentId}";
    private static string VersionsPath => $"/bff/documents/{BffTestFactory.StubDocumentId}/versions";
    private static string VersionPath(int v) => $"/bff/documents/{BffTestFactory.StubDocumentId}/versions/{v}";

    // ── 登録の門（構成でしか切り替わらないこと） ─────────────────────────────────

    // 🔴 未設定なら**何も登録しない**。REST が正であることの実体はこの 1 行である。
    [Fact]
    public void Registration_is_a_no_op_without_the_grpc_address()
    {
        var services = new ServiceCollection();
        services.AddDocumentReadGrpcClient(new ConfigurationBuilder().Build());

        services.Should().NotContain(d => d.ServiceType == typeof(DocumentReadGrpcClient));
    }

    // 陽性対照: アドレスが在れば登録される（上のテストだけだと「常に何もしない」実装でも緑になる）。
    [Fact]
    public void Registration_adds_the_client_when_the_address_is_configured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocumentReadGrpcClient(Config(DocumentReadGrpcClient.AddressKey, "http://document-service:8081"));

        services.Should().Contain(d => d.ServiceType == typeof(DocumentReadGrpcClient));
    }

    // 🔴 **BFF は 2 つの宛先を同時に持ち得る最初のホストである。**
    // 認可サービス宛のチャネルは `AddAuthzScopeGrpcClient` が**キー無し**で登録する。
    // 文書サービス宛を同じキー無しで登録すると、片方のクライアントがもう片方の宛先へ繋がる。
    // 文書側が**キー付き**であることを固定する（キー無しの登録は認可の 1 本だけ）。
    [Fact]
    public void Document_channel_is_keyed_so_it_does_not_collide_with_the_authz_channel()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AuthzScopeGrpcClient.AddressKey] = "http://authorization-service:8081",
            [DocumentReadGrpcClient.AddressKey] = "http://document-service:8081",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthzScopeGrpcClient(config);
        services.AddDocumentReadGrpcClient(config);

        services.Count(d => d.ServiceType == typeof(GrpcChannel) && d.ServiceKey is null)
            .Should().Be(1, "キー無しのチャネルは認可サービス宛の 1 本だけである");
        services.Should().Contain(d => d.ServiceType == typeof(GrpcChannel)
            && Equals(d.ServiceKey, DocumentReadGrpcClient.ChannelKey));
    }

    private static IConfiguration Config(string key, string value) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

    // ── 端点の振り替え（REST と gRPC が同じ答えを返すこと） ─────────────────────────

    // 既定（gRPC 未登録）では後段 HTTP スタブが呼ばれる。gRPC を登録すると**呼ばれない**。
    // 「gRPC が答えた」ことは、HTTP スタブが 500 を返す設定にしても 200 が返ることで示す。
    [Fact]
    public async Task Detail_uses_grpc_when_registered_and_does_not_touch_the_rest_stub()
    {
        _factory.DocumentStatusCode = HttpStatusCode.InternalServerError;

        // 陰性対照: gRPC が無ければ REST の 500 が 404（存在秘匿）へ落ちる。
        var viaRest = await _factory.CreateClient().GetAsync(DetailPath, TestContext.Current.CancellationToken);
        viaRest.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 陽性: gRPC が在れば REST を触らずに 200。
        var stub = new StubDocumentReadInvoker(_factory);
        var resp = await GrpcClient(stub).GetAsync(DetailPath, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken);
        body!.Title.Should().Be("経費規程 2025");
        stub.GetDocumentCalls.Should().Be(1);
    }

    // FR-05, [[IADR-0041]]: 🔴 **ABAC の実施点は移らない。**
    // gRPC 経路でも「スコープ外は 404 秘匿」がそのまま効く（判定は BFF 側に残っている）。
    [Fact]
    public async Task Detail_over_grpc_still_hides_documents_outside_the_scope()
    {
        _factory.SearchScopeGranted = false;

        var resp = await GrpcClient(new StubDocumentReadInvoker(_factory))
            .GetAsync(DetailPath, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // FR-06, SC-05: 一覧も同値（スコープ内の 1 件だけが見える。secret の 1 件は落ちる）。
    [Fact]
    public async Task List_over_grpc_matches_the_rest_result()
    {
        _factory.ScopeFilters = [new Platform.Shared.Contracts.Dtos.AttributeFilter("confidentiality", ["internal"])];

        var rest = await _factory.CreateClient()
            .GetFromJsonAsync<List<DocumentDto>>("/bff/documents", TestContext.Current.CancellationToken);
        var grpc = await GrpcClient(new StubDocumentReadInvoker(_factory))
            .GetFromJsonAsync<List<DocumentDto>>("/bff/documents", TestContext.Current.CancellationToken);

        rest.Should().ContainSingle("陽性対照: REST 側もスコープで 1 件へ絞られている");
        grpc.Should().BeEquivalentTo(rest, o => o.WithStrictOrdering());
    }

    // FR-06, UC-03: 版履歴・特定版も同値。
    [Fact]
    public async Task Versions_over_grpc_match_the_rest_result()
    {
        var client = GrpcClient(new StubDocumentReadInvoker(_factory));

        var versions = await client.GetFromJsonAsync<List<DocumentVersionDto>>(
            VersionsPath, TestContext.Current.CancellationToken);
        versions.Should().HaveCount(2);
        versions![0].Version.Should().Be(3);

        var snapshot = await client.GetFromJsonAsync<DocumentVersionDto>(
            VersionPath(3), TestContext.Current.CancellationToken);
        snapshot!.ChangeNote.Should().Be("第3条改定");

        var missing = await client.GetAsync(VersionPath(99), TestContext.Current.CancellationToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── 読み取りの主体（#1614） ───────────────────────────────────────────

    // NFR-09, FR-19, 計画 ADR-0119 決定 3, ADR-0086 決定 1 (#1614): 🔴 **gRPC の読み取り 4 口は、呼び出し元の
    // 利用者を本文の利用者文脈で運ぶ。** 運ばないと後段は BFF 自身（機械の主体）として読み、個人資料を返さない
    // ——所有者が自分の資料を SC-03 で開けなくなる。
    [Fact]
    public async Task Grpc_reads_carry_the_caller_as_the_user_context()
    {
        var stub = new StubDocumentReadInvoker(_factory);
        var client = GrpcClient(stub);
        client.DefaultRequestHeaders.Add(TestAuthHandler.UsernameHeader, "alice");

        (await client.GetAsync("/bff/documents", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.GetAsync(DetailPath, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.GetAsync(VersionsPath, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.GetAsync(VersionPath(3), TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        stub.Users.Keys.Should().BeEquivalentTo(["ListDocuments", "GetDocument", "ListVersions", "GetVersion"]);
        stub.Users.Values.Should().OnlyContain(u => u != null && u.UserId == "alice" && u.Action == "read",
            "4 口とも呼び出し元の利用者を運ぶ");
    }

    // 🔴 呼び出し元が機械（Bearer の無人主体）なら**運ばない**（BFF 自身の機械の主体として読まれ、個人資料は返らない）。
    // 機械の利用者名を利用者文脈として運ぶと、後段で「その名前の利用者」として照合されてしまう。
    [Fact]
    public async Task Grpc_reads_do_not_carry_a_user_context_for_a_machine_caller()
    {
        var stub = new StubDocumentReadInvoker(_factory);
        var client = GrpcClient(stub);
        client.DefaultRequestHeaders.Add(TestAuthHandler.UsernameHeader, "service-account-some-client");

        await client.GetAsync(DetailPath, TestContext.Current.CancellationToken);

        stub.Users.Should().ContainKey("GetDocument");
        stub.Users["GetDocument"].Should().BeNull();
    }

    // ── 縮退（呼び出し元ごとに向きが違う。一般化しない） ─────────────────────────

    // 一覧: 引けなかったら **空一覧**（現行 REST の catch と同じ枝を共有する）。
    [Fact]
    public async Task List_over_grpc_degrades_to_an_empty_list_when_the_call_fails()
    {
        var stub = new StubDocumentReadInvoker(_factory)
        {
            Failure = new RpcException(new Status(StatusCode.Unavailable, "stub")),
        };

        var resp = await GrpcClient(stub).GetAsync("/bff/documents", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<List<DocumentDto>>(TestContext.Current.CancellationToken))
            .Should().BeEmpty();
    }

    // 詳細: 引けなかったら **404 秘匿**（「不在」と区別しない。現行 REST と同じ枝）。
    [Fact]
    public async Task Detail_over_grpc_degrades_to_404_when_the_call_fails()
    {
        var stub = new StubDocumentReadInvoker(_factory)
        {
            Failure = new RpcException(new Status(StatusCode.Unavailable, "stub")),
        };

        var resp = await GrpcClient(stub).GetAsync(DetailPath, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 🔴 s2s トークンが取れないとき（構成不備・IdP 不達）も同じ枝へ落ちる。
    // 匿名で呼び直したりしない（載せるのは s2s トークンだけである）。
    [Fact]
    public async Task Detail_over_grpc_degrades_to_404_when_the_service_token_cannot_be_obtained()
    {
        var stub = new StubDocumentReadInvoker(_factory)
        {
            Failure = new InvalidOperationException("ServiceToken:ClientId / ClientSecret が未設定"),
        };

        var resp = await GrpcClient(stub).GetAsync(DetailPath, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 🔴 **版履歴の縮退は上の 2 つと向きが違う。** 現行 REST はここに捕捉を持たない
    // （引けなかったら 500）—— 「引けなかった」を「版が 0 件」に化けさせないための判断であり、
    // 輸送を替えても変えない（[[IADR-0256]] 決定 3 と同じ向き）。
    [Fact]
    public async Task Versions_over_grpc_do_not_hide_a_failure_as_an_empty_history()
    {
        var stub = new StubDocumentReadInvoker(_factory)
        {
            VersionsFailure = new RpcException(new Status(StatusCode.Unavailable, "stub")),
        };

        // TestServer は未処理例外を握り潰さず呼び出し側へ再送出する（実配備では 500 になる枝）。
        // **空の版履歴（200 `[]`）にはならない**ことがここでの不変条件である。
        var act = async () => await GrpcClient(stub)
            .GetAsync(VersionsPath, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.Unavailable);
    }

    // gRPC クライアントを DI へ差し込んだテスト用ホスト。**構成キーではなく DI へ直接入れる** ——
    // 実チャネルを張らずに `DocumentBffEndpoints` の経路選択（登録の有無）だけを測るためである。
    private HttpClient GrpcClient(StubDocumentReadInvoker stub) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton(new DocumentReadGrpcClient(new Pb.DocumentRead.DocumentReadClient(stub)))))
            .CreateClient();

    // 生成クライアントは CallInvoker の上に乗る。器の HTTP スタブと**同じ状態**から応答を組み、
    // REST と gRPC が同じ答えを返すことを比べられるようにする。
    private sealed class StubDocumentReadInvoker(BffTestFactory factory) : CallInvoker
    {
        public int GetDocumentCalls { get; private set; }

        /// <summary>#1614: rpc ごとに受け取った利用者文脈（載っていなければ null）。</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, Pb.UserContext?> Users { get; } = new();

        /// <summary>全 rpc を失敗させる（縮退の検証用）。</summary>
        public Exception? Failure { get; init; }

        /// <summary>版履歴だけを失敗させる（縮退の向きが違うことの検証用）。</summary>
        public Exception? VersionsFailure { get; init; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            try
            {
                return Ok<TResponse>(Respond(method.Name, request!));
            }
            catch (Exception ex) when (ex is RpcException or InvalidOperationException)
            {
                return Failed<TResponse>(ex);
            }
        }

        private object Respond(string name, object request)
        {
            Users[name] = request switch
            {
                Pb.ListDocumentsRequest r => r.User,
                Pb.GetDocumentRequest r => r.User,
                Pb.ListVersionsRequest r => r.User,
                Pb.GetVersionRequest r => r.User,
                _ => null,
            };
            if (Failure is not null) throw Failure;

            switch (name)
            {
                case nameof(Pb.DocumentRead.DocumentReadClient.ListDocuments):
                    {
                        var resp = new Pb.ListDocumentsResponse();
                        resp.Documents.AddRange(factory.StubDocumentList.Select(DocumentReadGrpcMapping.ToProto));
                        return resp;
                    }
                case nameof(Pb.DocumentRead.DocumentReadClient.GetDocument):
                    {
                        GetDocumentCalls++;
                        var id = ((Pb.GetDocumentRequest)request).Id;
                        var doc = factory.StubDocumentList
                            .Concat([factory.StubDocument])
                            .FirstOrDefault(d => d.Id == Guid.Parse(id));
                        return doc is null
                            ? new Pb.GetDocumentResponse { Found = false }
                            : new Pb.GetDocumentResponse { Found = true, Document = DocumentReadGrpcMapping.ToProto(doc) };
                    }
                case nameof(Pb.DocumentRead.DocumentReadClient.ListVersions):
                    {
                        if (VersionsFailure is not null) throw VersionsFailure;
                        var resp = new Pb.ListVersionsResponse { Found = true };
                        resp.Versions.AddRange(factory.StubVersions.Select(DocumentReadGrpcMapping.ToProto));
                        return resp;
                    }
                case nameof(Pb.DocumentRead.DocumentReadClient.GetVersion):
                    {
                        if (VersionsFailure is not null) throw VersionsFailure;
                        var req = (Pb.GetVersionRequest)request;
                        var snapshot = factory.StubVersions.FirstOrDefault(v => v.Version == req.Version);
                        return snapshot is null
                            ? new Pb.GetVersionResponse { Found = false }
                            : new Pb.GetVersionResponse { Found = true, Snapshot = DocumentReadGrpcMapping.ToProto(snapshot) };
                    }
                default:
                    throw new NotSupportedException(name);
            }
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
