using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Knowledge.Bff.Endpoints.Documents;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Knowledge.Contracts.Grpc.Document.V1;

namespace Platform.Bff.Tests;

// FR-06, UC-03, SC-05, NFR-09, NFR-16, ADR-0029 (#1897): 文書一覧の gRPC 応答（`ListDocuments`）が
// 受信上限を超えて空一覧に化けた件の応急処置を固定する。
//
// - (b) D-1（BFF → document）のチャネルだけ受信上限を構成で変えられ、既定は 64 MiB である。
//   共有の `CreatePlatformChannel` の既定（4 MB）は他の呼び出し元のために変えない。
// - (c) BFF が失敗を空一覧・404 へ畳むとき、状態・経路・例外の型を WARN で 1 行出す。秘密情報・本文は出さない。
//
// 受信上限の検証は**本物のチャネル**（127.0.0.1 の実サーバー）で行う —— 上限は `GrpcChannel` の中で効くので、
// 偽の CallInvoker では測れない。
public class BffDocumentReadReceiveLimitTests : IClassFixture<BffTestFactory>
{
    // #1895 で graph が孤立文書として数えた件数（稼働の台帳の下限）。
    private const int LedgerLowerBound = 38_703;
    private const int GrpcDefaultMaxReceive = 4 * 1024 * 1024;

    private readonly BffTestFactory _factory;

    public BffDocumentReadReceiveLimitTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        _factory.ScopeFilters = [];
        _factory.ScopeBranches = null;
        _factory.DocumentStatusCode = HttpStatusCode.OK;
    }

    // ── (b) 既定値の根拠 ───────────────────────────────────────────────

    // 既定 64 MiB の根拠を数で固定する: 代表的な 1 件 × 台帳の下限は 4 MB を超え（＝事象が再現する）、
    // 64 MiB には 2 倍以上の余裕を持って収まる。代表的な 1 件の形を変えたらここで見直す。
    [Fact]
    public void Default_limit_holds_the_current_ledger_with_headroom_while_4MB_does_not()
    {
        var perDocument = RepresentativeSummary(0).CalculateSize() + 3; // repeated の tag ＋ 長さ（2 バイト）

        perDocument.Should().BeInRange(300, 700, "代表的な 1 件は約 450 バイト（作業仕様書の算定）");
        ((long)perDocument * LedgerLowerBound).Should().BeGreaterThan(GrpcDefaultMaxReceive,
            "★ 陽性対照 —— grpc-dotnet の既定 4 MB では稼働の台帳が収まらない（#1897 の事象）");
        ((long)perDocument * LedgerLowerBound * 2).Should().BeLessThan(DocumentReadGrpcClient.DefaultMaxReceiveMessageSize,
            "既定は稼働の台帳の 2 倍以上を収める");
    }

    // ── (b) 構成した上限が実際に効く ────────────────────────────────────────

    // 🔴 構成した上限が**チャネルに届いている**ことを、上限の内外の対で示す（同じ応答・同じサーバー）。
    [Fact]
    public async Task Configured_limit_is_enforced_by_the_document_channel()
    {
        var service = new LargeListService(count: 2_000); // 約 1 MB
        await using var server = await LoopbackGrpcServer.StartAsync(service, TestContext.Current.CancellationToken);
        var size = service.Response.CalculateSize();

        var tooSmall = ResolveClient(server, maxReceive: (size / 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var tight = async () => await tooSmall.ListAsync(Anonymous, TestContext.Current.CancellationToken);
        (await tight.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.ResourceExhausted, "構成した上限（応答の半分）を超えた");

        var enough = ResolveClient(server, maxReceive: (size * 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
        (await enough.ListAsync(Anonymous, TestContext.Current.CancellationToken))
            .Should().HaveCount(2_000, "★ 陽性対照 —— 上限の内側なら同じ応答が通る");
    }

    // 未設定なら既定（64 MiB）が効き、4 MB を超える応答も受け取れる。
    // 対として、上限を渡さない共有の `CreatePlatformChannel`（他の呼び出し元の形）は従来どおり 4 MB で切る。
    [Fact]
    public async Task Default_document_channel_accepts_a_response_above_4MB_while_the_shared_default_still_cuts_it()
    {
        var service = new LargeListService(count: 10_000);
        await using var server = await LoopbackGrpcServer.StartAsync(service, TestContext.Current.CancellationToken);
        service.Response.CalculateSize().Should().BeGreaterThan(GrpcDefaultMaxReceive, "★ 前提 —— 応答は 4 MB を超える");

        var docs = await ResolveClient(server, maxReceive: null).ListAsync(Anonymous, TestContext.Current.CancellationToken);
        docs.Should().HaveCount(10_000);

        using var shared = GrpcClientExtensions.CreatePlatformChannel(
            server.Channel.Target.StartsWith("http", StringComparison.Ordinal) ? server.Channel.Target : $"http://{server.Channel.Target}",
            new BffTestFactory.FixedServiceTokenProvider());
        var act = async () => await new Pb.DocumentRead.DocumentReadClient(shared)
            .ListDocumentsAsync(new Pb.ListDocumentsRequest(), cancellationToken: TestContext.Current.CancellationToken);
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.ResourceExhausted, "共有部品の既定（4 MB）は変えていない");
    }

    // 🔴 0 以下・数値でない値は起動時（登録時）に落とす。黙って既定へ戻すと、構成の誤りが「一覧が空」として再発する。
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("64MB")]
    public void Invalid_limit_fails_at_registration(string value)
    {
        var act = () => new ServiceCollection().AddDocumentReadGrpcClient(Config("http://document-service:8081", value));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{DocumentReadGrpcClient.MaxReceiveMessageSizeKey}*");
    }

    // 受け入れ基準 1: 台帳が 4 MB 相当を超えても、`GET /bff/documents` が gRPC 経路で権限内の文書を返す（空一覧に化けない）。
    [Fact]
    public async Task Bff_document_list_over_a_real_channel_is_not_empty_when_the_response_exceeds_4MB()
    {
        var service = new LargeListService(count: 10_000);
        await using var server = await LoopbackGrpcServer.StartAsync(service, TestContext.Current.CancellationToken);
        var sink = new ConcurrentQueue<(LogLevel Level, string Category, string Message)>();
        var grpc = ResolveClient(server, maxReceive: null);

        var http = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(s => s.AddSingleton(grpc));
            b.ConfigureLogging(l => l.AddProvider(new CollectingLoggerProvider(sink)));
        }).CreateClient();
        var docs = await http.GetFromJsonAsync<List<DocumentDto>>("/bff/documents", TestContext.Current.CancellationToken);

        docs.Should().HaveCount(10_000);
        sink.Should().NotContain(e => e.Category == DocumentReadFailureLogCategory, "成功した呼び出しで WARN を出さない");
    }

    // ── (c) 畳むときの WARN ─────────────────────────────────────────────

    // 受け入れ基準 3（陽性）: 一覧が上限超過（ResourceExhausted）を空一覧へ畳むとき、状態・経路・例外の型を含む WARN を 1 行出す。
    // 🔴 status の detail（サーバ・ライブラリの文言）は載せない。
    [Fact]
    public async Task List_fold_on_resource_exhausted_logs_one_warning_with_status_route_and_type()
    {
        var sink = new ConcurrentQueue<(LogLevel Level, string Category, string Message)>();
        var stub = new FailingInvoker(new RpcException(new Status(StatusCode.ResourceExhausted, "detail-must-not-leak")));

        var resp = await HostWith(stub, sink).GetAsync("/bff/documents", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<List<DocumentDto>>(TestContext.Current.CancellationToken)).Should().BeEmpty();
        var warning = sink.Where(e => e.Category == DocumentReadFailureLogCategory).Should().ContainSingle().Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("ListDocuments").And.Contain("grpc")
            .And.Contain("ResourceExhausted").And.Contain(nameof(RpcException)).And.Contain("空一覧");
        warning.Message.Should().NotContain("detail-must-not-leak");
    }

    // 受け入れ基準 3（陽性・詳細）: 詳細が 404 へ畳むときも同じ形の WARN を出す。
    // 🔴 s2s トークン取得失敗のメッセージ（IdP の応答を含み得る）は載せない。
    [Fact]
    public async Task Detail_fold_logs_one_warning_without_the_exception_message()
    {
        var sink = new ConcurrentQueue<(LogLevel Level, string Category, string Message)>();
        var stub = new FailingInvoker(new InvalidOperationException("client_secret=must-not-leak"));

        var resp = await HostWith(stub, sink).GetAsync(
            $"/bff/documents/{BffTestFactory.StubDocumentId}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var warning = sink.Where(e => e.Category == DocumentReadFailureLogCategory).Should().ContainSingle().Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("GetDocument").And.Contain(nameof(InvalidOperationException)).And.Contain("404");
        warning.Message.Should().NotContain("must-not-leak");
    }

    // 受け入れ基準 3（陰性）: 成功した一覧では WARN を出さない（通常操作でログが溢れて本当の縮退が埋もれないように）。
    [Fact]
    public async Task Successful_list_logs_no_warning()
    {
        var sink = new ConcurrentQueue<(LogLevel Level, string Category, string Message)>();

        var resp = await HostWith(new FailingInvoker(failure: null), sink)
            .GetAsync("/bff/documents", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<List<DocumentDto>>(TestContext.Current.CancellationToken))
            .Should().ContainSingle("★ 陽性対照 —— 応答が実際に通っている");
        sink.Should().NotContain(e => e.Category == DocumentReadFailureLogCategory);
    }

    // ── 器 ──────────────────────────────────────────────────────────────

    // `DocumentReadFailureLog.Category` は internal なので、運用者が grep する語として値を写す（変えたらここも変わる）。
    private const string DocumentReadFailureLogCategory = "Knowledge.Bff.Endpoints.DocumentBffEndpoints";

    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    private static IConfiguration Config(string address, string? maxReceive)
    {
        var values = new Dictionary<string, string?> { [DocumentReadGrpcClient.AddressKey] = address };
        if (maxReceive is not null)
            values[DocumentReadGrpcClient.MaxReceiveMessageSizeKey] = maxReceive;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // 本番と同じ登録（`AddDocumentReadGrpcClient`）でチャネルを作る。s2s トークンだけ固定値へ差し替える。
    private static DocumentReadGrpcClient ResolveClient(LoopbackGrpcServer server, string? maxReceive)
    {
        var address = server.Channel.Target.StartsWith("http", StringComparison.Ordinal)
            ? server.Channel.Target
            : $"http://{server.Channel.Target}";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Platform.Shared.Infrastructure.Foundation.Grpc.IServiceTokenProvider>(
            new BffTestFactory.FixedServiceTokenProvider());
        services.AddDocumentReadGrpcClient(Config(address, maxReceive));
        return services.BuildServiceProvider().GetRequiredService<DocumentReadGrpcClient>();
    }

    private HttpClient HostWith(CallInvoker invoker, ConcurrentQueue<(LogLevel, string, string)> sink) =>
        _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(s => s.AddSingleton(new DocumentReadGrpcClient(new Pb.DocumentRead.DocumentReadClient(invoker))));
            b.ConfigureLogging(l => l.AddProvider(new CollectingLoggerProvider(sink)));
        }).CreateClient();

    // 代表的な 1 件（作業仕様書の算定と同じ形）: 日本語の表題 30 字・storage:// の参照・属性 4 つ・タグ 3 つ・指紋 64 字。
    private static Pb.DocumentSummary RepresentativeSummary(int i)
    {
        var now = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
        var id = new Guid(i, 0, 0, new byte[8]).ToString("D");
        var summary = new Pb.DocumentSummary
        {
            Id = id,
            Title = new string('文', 30),
            Status = "published",
            MarkdownUri = $"storage://documents/{id}/normalized.md",
            Version = 3,
            CreatedAt = now,
            UpdatedAt = now,
            HasBody = true,
            ContentFingerprint = new string('a', 64),
        };
        summary.Attributes["confidentiality"] = "internal";
        summary.Attributes["department"] = "engineering";
        summary.Attributes["owner"] = Guid.Empty.ToString("D");
        summary.Attributes["doc_scope"] = "org";
        summary.Tags.Add("規程");
        summary.Tags.Add("経費");
        summary.Tags.Add("engineering");
        return summary;
    }

    // 受け口の偽物（生成された `*Base` の派生）。応答は前もって組んでおく。
    private sealed class LargeListService : Pb.DocumentRead.DocumentReadBase
    {
        public LargeListService(int count)
        {
            Response = new Pb.ListDocumentsResponse();
            Response.Documents.AddRange(Enumerable.Range(0, count).Select(RepresentativeSummary));
        }

        public Pb.ListDocumentsResponse Response { get; }

        public override Task<Pb.ListDocumentsResponse> ListDocuments(Pb.ListDocumentsRequest request, ServerCallContext context)
            => Task.FromResult(Response);
    }

    // 一覧・詳細を失敗させる（`failure` が null なら 1 件を返す）偽のクライアント。
    private sealed class FailingInvoker(Exception? failure) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            if (failure is not null)
                return new(Task.FromException<TResponse>(failure), Task.FromResult(new Metadata()),
                    () => failure is RpcException rpc ? rpc.Status : Status.DefaultSuccess, () => new Metadata(), () => { });

            object response = method.Name switch
            {
                nameof(Pb.DocumentRead.DocumentReadClient.ListDocuments) => ListOfOne(),
                _ => throw new NotSupportedException(method.Name),
            };
            return new(Task.FromResult((TResponse)response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }

        private static Pb.ListDocumentsResponse ListOfOne()
        {
            var resp = new Pb.ListDocumentsResponse();
            resp.Documents.Add(RepresentativeSummary(1));
            return resp;
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();
    }

    private sealed class CollectingLoggerProvider(ConcurrentQueue<(LogLevel, string, string)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Collector(sink, categoryName);

        public void Dispose()
        {
        }

        private sealed class Collector(ConcurrentQueue<(LogLevel, string, string)> sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            // 例外本体も連結する —— 「例外を添えていない」ことも NotContain で検査できるようにする。
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                sink.Enqueue((logLevel, category, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
        }
    }
}
