using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Tests.Testing;
using System.Net;
using System.Security.Claims;
using System.Text;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Authz;

// FR-05, NFR-09, ADR-0004, ADR-0036, [[IADR-0379]] 決定 5, [[IADR-0413]] (#1378):
// `BffScopeResolver.ResolveAsync` の **REST 経路が deny-by-default へ縮退した理由を WARN で出す**ことを固定する。
//
// 🔴 **#1378 の実害**: 稼働環境で人の利用者の一覧が 0 件・作成が 403 になったが、REST 経路は
// 非 2xx・不達・空本文を**無言で** null へ畳んでいたため「0 件」「403」としか見えず、切り分けに
// 1 時間以上を要した。gRPC 経路（`AuthzScopeGrpcClient`）は同じ縮退を WARN で出している。
//
// 🔴 **陰性対照が要る。** `Granted=false` は正当な deny であり、ここで WARN を出すと通常操作で
// ログが溢れて本当の縮退が埋もれる。「常に WARN を出す」壊れた実装を落とすため必ず対にする。
//
// 戻り値が従来どおり null であることも併せて表明する（挙動は変えない）。
[Trait("TestKind", "Unit")]
public class BffScopeResolveWarnTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Category = typeof(BffScopeResolver).FullName!;

    // 要求の DI から得たロガーを記録する器（`BffScopeResolver` は静的なので `ILoggerFactory` から引く）。
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly Dictionary<string, RecordingLogger<object>> _loggers = [];

        public RecordingLogger<object> Of(string category) =>
            _loggers.TryGetValue(category, out var l) ? l : new RecordingLogger<object>();

        public ILogger CreateLogger(string categoryName)
        {
            if (!_loggers.TryGetValue(categoryName, out var logger))
                _loggers[categoryName] = logger = new RecordingLogger<object>();
            return logger;
        }

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://authorization-service") };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpContext Ctx(IServiceProvider services) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "alice"), new Claim("clearance", "internal")], authenticationType: "test")),
        RequestServices = services,
    };

    private static (HttpContext Ctx, RecordingLoggerFactory Logs) WithLogs()
    {
        var logs = new RecordingLoggerFactory();
        var sp = new ServiceCollection().AddSingleton<ILoggerFactory>(logs).BuildServiceProvider();
        return (Ctx(sp), logs);
    }

    private static async Task<BffAccessScope?> ResolveAsync(
        HttpContext ctx, Func<HttpRequestMessage, HttpResponseMessage> respond, string action = BffScopeAction.Read)
        => await BffScopeResolver.ResolveAsync(new SingleClientFactory(new StubHandler(respond)), ctx, action, Ct);

    private static object? Field(RecordingLogger<object>.Entry e, string key) =>
        e.State.FirstOrDefault(p => p.Key == key).Value;

    // T-01（a）: 非 2xx は状態コードつきの WARN を 1 行出し、null（deny）へ縮退する。
    // 403 は `ServiceCaller` の門（s2s トークンの不備）、400 は値域外の action、503 は認可サービスの障害。
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_non_success_status_is_logged_as_a_warning_with_the_status(HttpStatusCode status)
    {
        var (ctx, logs) = WithLogs();

        var scope = await ResolveAsync(ctx, _ => new HttpResponseMessage(status), BffScopeAction.Write);

        scope.Should().BeNull("挙動は変えない —— 非 2xx は従来どおり deny-by-default");
        var warn = logs.Of(Category).OfLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        Field(warn, "Status").Should().Be((int)status);
        warn.Message.Should().Contain("deny-by-default");
    }

    // T-02（b）: 不達（接続失敗・タイムアウト）は例外の型つきで WARN を出し、例外本体を添える。
    [Fact]
    public async Task A_transport_failure_is_logged_as_a_warning_with_the_exception_type()
    {
        var (ctx, logs) = WithLogs();
        var refused = new HttpRequestException("Connection refused (authorization-service:5005)");

        var scope = await ResolveAsync(ctx, _ => throw refused);

        scope.Should().BeNull();
        var warn = logs.Of(Category).OfLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        Field(warn, "ErrorType").Should().Be(nameof(HttpRequestException));
        warn.Exception.Should().BeSameAs(refused);
    }

    [Fact]
    public async Task A_timeout_is_logged_as_a_warning_with_the_exception_type()
    {
        var (ctx, logs) = WithLogs();

        var scope = await ResolveAsync(ctx, _ => throw new TaskCanceledException("timeout"));

        scope.Should().BeNull();
        Field(logs.Of(Category).OfLevel(LogLevel.Warning).Should().ContainSingle().Subject, "ErrorType")
            .Should().Be(nameof(TaskCanceledException));
    }

    // 🔴 T-03（b・#1378 の実際の形）: **s2s トークンが取れない**（`ServiceToken:ClientId` 未設定など）。
    // `ServiceTokenHandler` がこれを `HttpRequestException` へ畳むので、**理由は内側の例外にしか無い**。
    // 本物の登録（`AddPlatformAuthzScopeHttpClient`）を通し、WARN が内側の理由まで運ぶことを見る。
    [Fact]
    public async Task A_service_token_failure_is_logged_with_the_inner_reason()
    {
        var logs = new RecordingLoggerFactory();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(logs);
        services.AddPlatformAuthzScopeHttpClient(new ConfigurationBuilder().Build());
        services.RemoveAll<IServiceTokenProvider>();
        var reason = new InvalidOperationException("ServiceToken:ClientId / ClientSecret が未設定です。");
        services.AddSingleton<IServiceTokenProvider>(new ThrowingToken(reason));
        services.AddHttpClient(AuthzScopeHttpClient.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ =>
                throw new InvalidOperationException("トークンが無いまま認可サービスへ届いてはならない")));
        await using var sp = services.BuildServiceProvider();

        var scope = await BffScopeResolver.ResolveAsync(
            sp.GetRequiredService<IHttpClientFactory>(), Ctx(sp), BffScopeAction.Read, Ct);

        scope.Should().BeNull();
        var warn = logs.Of(Category).OfLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        warn.Exception.Should().BeOfType<HttpRequestException>()
            .Which.InnerException.Should().BeSameAs(reason, "縮退の理由は内側の例外にある");
    }

    // T-04（c）: 2xx だが本文が空（JSON の null）。
    [Fact]
    public async Task An_empty_body_is_logged_as_a_warning()
    {
        var (ctx, logs) = WithLogs();

        var scope = await ResolveAsync(ctx, _ => Json(HttpStatusCode.OK, "null"));

        scope.Should().BeNull();
        var warn = logs.Of(Category).OfLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        Field(warn, "Status").Should().Be(200);
    }

    // 🔴 T-05（陰性対照）: **`Granted=false` は正当な deny であり WARN を出さない。**
    [Fact]
    public async Task A_legitimate_deny_is_not_logged_as_a_warning()
    {
        var (ctx, logs) = WithLogs();

        var scope = await ResolveAsync(ctx, _ => Json(HttpStatusCode.OK,
            """{"userId":"alice","allowedFilters":[],"granted":false}"""));

        scope.Should().BeNull();
        logs.Of(Category).OfLevel(LogLevel.Warning).Should().BeEmpty();
    }

    // 陽性対照: 許可応答でも WARN を出さない（許可を許可として返せる）。
    [Fact]
    public async Task A_grant_is_not_logged_as_a_warning()
    {
        var (ctx, logs) = WithLogs();

        var scope = await ResolveAsync(ctx, _ => Json(HttpStatusCode.OK,
            """{"userId":"alice","allowedFilters":[],"granted":true}"""));

        scope.Should().NotBeNull();
        logs.Of(Category).OfLevel(LogLevel.Warning).Should().BeEmpty();
    }

    // 🔴 T-06: **利用者 ID・属性をログへ載せない**（gRPC 側の WARN と同じ）。
    [Fact]
    public async Task The_warning_does_not_carry_the_user_or_attributes()
    {
        var (ctx, logs) = WithLogs();

        await ResolveAsync(ctx, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var warn = logs.Of(Category).OfLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        warn.Message.Should().NotContain("alice").And.NotContain("internal");
        warn.State.Select(p => p.Value?.ToString()).Should().NotContain(["alice", "internal"]);
    }

    // 呼び出し元の DI にロガーが無くても（直接構築の試験など）落ちず、従来どおり null を返す。
    [Fact]
    public async Task Without_a_logger_factory_it_still_degrades_to_null()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test")),
        };

        var scope = await ResolveAsync(ctx, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        scope.Should().BeNull();
    }

    private sealed class ThrowingToken(Exception ex) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct) => ValueTask.FromException<string>(ex);
    }
}
