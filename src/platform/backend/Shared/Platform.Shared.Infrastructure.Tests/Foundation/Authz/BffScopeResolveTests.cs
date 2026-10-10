using AwesomeAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Authz;
using System.Security.Claims;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Authz;

// FR-05, FR-06, FR-19, ADR-0036, IADR-0009, IADR-0253, IADR-0272 (#901),
// 計画 ADR-0089 決定 1, [[IADR-0533]] (#1255): BffScopeResolver.ResolveAsync を共有ライブラリ側で固定する。
//
// 🔴 **なぜここに要るのか。** Platform.Bff.Tests/BffScopeResolverTests.cs は純ロジックの Matches と
// ExtractUserAttributes を直接検証しているが、ResolveAsync の経路は検証していない。
// ここが緩むと **全 BFF 経路の認可が同時に緩む**（本ライブラリは全サービスが依存する）。
//
// ［2026-10-10 / #1255・[[IADR-0533]]］解決の輸送は east-west gRPC（`AuthzScope/Resolve`）だけになった
// （REST `POST /authz/scope` の並走は撤去した）。従前の REST 版の試験を gRPC の上へ写した ——
// ダブルは「認可サービスの応答」（生成クライアントの下の CallInvoker）だけに留める。
// REST 固有の枝（非 2xx・空本文・HttpRequestException）は消え、対応する gRPC の枝（全 status・s2s トークン取得失敗・
// 宛先未構成の `UNAVAILABLE`）で同じ deny-by-default を固定する。
public class BffScopeResolveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 認可サービスの応答を差し替える CallInvoker。要求を捕捉し「何を送ったか」も検査できる。
    private sealed class StubInvoker(
        Func<Pb.ResolveScopeRequest, CancellationToken, Pb.ResolveScopeResponse> respond) : CallInvoker
    {
        public Pb.ResolveScopeRequest? Captured { get; private set; }
        public int CallCount { get; private set; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            CallCount++;
            Captured = (Pb.ResolveScopeRequest)(object)request!;
            Task<TResponse> response;
            try
            {
                response = Task.FromResult((TResponse)(object)respond(Captured, options.CancellationToken));
            }
            catch (Exception ex)
            {
                response = Task.FromException<TResponse>(ex);
            }
            return new AsyncUnaryCall<TResponse>(
                response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
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

    // REST 経路のための引数であり、いまは使われない。呼ばれたら試験を落とす（REST へ戻っていないことの観測点）。
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("REST 経路が呼ばれた");
    }

    private static HttpContext Ctx(StubInvoker invoker, string? name = "alice", params (string Key, string Value)[] claims)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AuthzScopeGrpcClient(
            new Pb.AuthzScope.AuthzScopeClient(invoker), NullLogger<AuthzScopeGrpcClient>.Instance));
        var list = claims.Select(c => new Claim(c.Key, c.Value)).ToList();
        if (name is not null) list.Add(new Claim(ClaimTypes.Name, name));
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(list, authenticationType: "test")),
        };
    }

    private static Task<BffAccessScope?> Resolve(HttpContext ctx, string action = BffScopeAction.Read, CancellationToken? ct = null)
        => BffScopeResolver.ResolveAsync(new ThrowingHttpClientFactory(), ctx, action, ct ?? Ct);

    private static Pb.AttributeFilter Filter(string key, params string[] values)
    {
        var f = new Pb.AttributeFilter { Key = key };
        f.AllowedValues.AddRange(values);
        return f;
    }

    // ── 許可される場合 ────────────────────────────────────────────────────────

    // FR-05: Granted=true の応答は BffAccessScope として返る（フィルタを保持する）。
    [Fact]
    public async Task 許可応答はフィルタを保持したスコープとして返る()
    {
        var invoker = new StubInvoker((_, _) => new Pb.ResolveScopeResponse
        {
            UserId = "alice",
            Granted = true,
            AllowedFilters = { Filter("department", "sales") },
        });

        var scope = await Resolve(Ctx(invoker));

        scope.Should().NotBeNull();
        scope!.GrantsAccess.Should().BeTrue();
        scope.Filters.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new AttributeFilter("department", ["sales"]));
    }

    // FR-19, IADR-0253 決定 1（段 3 / #989）: **Branches を運ぶ**。
    [Fact]
    public async Task 許可応答の名前つき分岐は後段の契約型まで運ばれる()
    {
        var invoker = new StubInvoker((_, _) => new Pb.ResolveScopeResponse
        {
            UserId = "alice",
            Granted = true,
            Branches =
            {
                new Pb.AccessScopeBranch { Name = "owner", Filters = { Filter("owner", "alice") } },
                new Pb.AccessScopeBranch { Name = "attribute", Filters = { Filter("department", "sales") } },
            },
        });

        var scope = await Resolve(Ctx(invoker));

        scope.Should().NotBeNull();
        scope!.Branches.Should().HaveCount(2);
        scope.Branches!.Select(b => b.Name).Should().BeEquivalentTo(["owner", "attribute"]);
        scope.ToContractScope().Branches.Should().HaveCount(2);
    }

    // ── deny-by-default へ縮退する経路 ────────────────────────────────────────
    //
    // 🔴 いずれも「null を返す」ため、1 つだけ書いても「常に null を返す」壊れた実装が通る。
    // 上の許可系 2 件が対照条件である。

    // FR-05: 許可ポリシーが無い（Granted=false）＝閲覧可能なし。フィルタが空でも全件開放ではない。
    [Fact]
    public async Task 未許可応答はフィルタが空でもnullへ縮退する()
    {
        var invoker = new StubInvoker((_, _) => new Pb.ResolveScopeResponse { UserId = "alice", Granted = false });

        var scope = await Resolve(Ctx(invoker));

        scope.Should().BeNull("granted=false でフィルタ空を『条件なし全件許可』と読むと全開放になる");
    }

    // FR-05: 認可サービスが答えた上での失敗・届かない・期限切れは **例外を投げず** deny-by-default。
    // 🔴 投げると BFF が 500 を返し、可用性障害が「全断」に化ける。握って許可へ倒すと障害時に全開放になる。
    [Theory]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.InvalidArgument)]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task 認可サービスの失敗は例外を投げずnullへ縮退する(StatusCode status)
    {
        var invoker = new StubInvoker((_, _) => throw new RpcException(new Status(status, "stub")));

        var scope = await Resolve(Ctx(invoker));

        scope.Should().BeNull();
    }

    // FR-05: s2s トークンが取れない（構成不備・IdP 不達）ときも匿名で呼ばず deny へ倒す。
    [Fact]
    public async Task サービス間トークンが取れなければnullへ縮退する()
    {
        var invoker = new StubInvoker((_, _) => throw new InvalidOperationException("ServiceToken:ClientId が未設定です。"));

        var scope = await Resolve(Ctx(invoker));

        scope.Should().BeNull();
    }

    // 🔴 FR-05: **呼び出し元のキャンセルは deny へ化けさせない。**
    [Fact]
    public async Task 呼び出し元がキャンセル済みなら例外を伝播しdenyへ化けさせない()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var invoker = new StubInvoker((_, ct) => throw new RpcException(new Status(StatusCode.Cancelled, "cancelled")));

        var act = async () => await Resolve(Ctx(invoker), ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "キャンセルを null（＝閲覧可能なし）へ縮退すると、打ち切りが空応答の成功に化ける");
    }

    // 🔴 [[IADR-0533]] 決定 2: **宛先が構成されていない配備では deny-by-default** である（REST へ戻らない）。
    // 登録は宛先の有無に関わらず行われ、未構成なら常に `UNAVAILABLE` を返す呼び出し器の上に組まれる。
    [Fact]
    public async Task 宛先が構成されていなければnullへ縮退しRESTへ戻らない()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthzScopeGrpcClient(new ConfigurationBuilder().Build());
        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test")),
        };

        var scope = await Resolve(http);

        scope.Should().BeNull("宛先が無いのは「届かない」と同じ deny であり、REST の HttpClient は呼ばれない（呼ばれると例外）");
    }

    // 🔴 登録が無い（組み立ての誤り）なら deny へ倒し、理由を WARN で出す。
    [Fact]
    public async Task クライアントが登録されていなければWARNを出してnullへ縮退する()
    {
        var logs = new RecordingLoggerFactory();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<ILoggerFactory>(logs).BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test")),
        };

        var scope = await Resolve(http);

        scope.Should().BeNull();
        logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("AddAuthzScopeGrpcClient"));
    }

    // ── 要求本文（権限昇格の防止） ────────────────────────────────────────────

    // 🔴 FR-05: **利用者はサーバ側の HttpContext.User から決める。** action は引数どおり運ぶ（IADR-0272 決定 4 / #1010）。
    [Fact]
    public async Task 要求本文の利用者はサーバ側のIDで属性とアクションを伴って送られる()
    {
        var invoker = new StubInvoker((_, _) => new Pb.ResolveScopeResponse { UserId = "alice", Granted = true });

        await Resolve(Ctx(invoker, "alice", ("clearance", "secret"), ("department", "sales")), BffScopeAction.Write);

        invoker.Captured!.UserId.Should().Be("alice");
        invoker.Captured.Action.Should().Be("write",
            "書き込み経路が read のスコープで判定されると #1010 の欠陥が再発する");
        invoker.Captured.UserAttributes["clearance"].Should().Be("secret");
        invoker.Captured.UserAttributes["department"].Should().Be("sales");
    }

    // FR-05: 未認証（Identity.Name が無い）は "anonymous" として解決へ送る（呼び出し側で例外にならない）。
    [Fact]
    public async Task 未認証の利用者はanonymousとして送られる()
    {
        var invoker = new StubInvoker((_, _) => new Pb.ResolveScopeResponse { UserId = "anonymous", Granted = false });

        var scope = await Resolve(Ctx(invoker, name: null));

        invoker.Captured!.UserId.Should().Be("anonymous");
        scope.Should().BeNull();
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class Logger(RecordingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner.Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
