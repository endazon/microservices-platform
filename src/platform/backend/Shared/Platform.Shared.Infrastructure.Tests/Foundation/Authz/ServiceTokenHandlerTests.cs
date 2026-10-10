using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Foundation.Llm;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Authz;

// NFR-09, ADR-0084 決定 1, [[IADR-0379]] 決定 4, [[IADR-0413]], [[IADR-0424]], [[IADR-0533]] (#1333, #1364, #1255):
// REST の発信要求へ s2s トークンを付ける `ServiceTokenHandler` の不変条件を固定する。
//
// ［2026-10-10 / #1255・[[IADR-0533]]］従前は認可スコープ解決の REST クライアント（`AuthzScopeHttpClient`）の試験だった。
// そのクライアントは撤去し、ハンドラの利用者は gRPC へ移っていない LlmGateway の REST 面（`AddLlmGatewayServiceToken`。
// GraphService のクラスタ要約）だけになったので、同じ不変条件をその登録の上で測る。
//
// 1. **s2s トークンを付ける**（受け口が `ServiceCaller` を要る）
// 2. **トークン取得失敗を `HttpRequestException` へ畳む**（呼び出し元の縮退の枝へ合流させる）
// 3. **呼び出し元の取り消しだけは伝播する**
[Trait("TestKind", "Unit")]
public class ServiceTokenHandlerTests
{
    private const string ClientName = "LlmGatewayRestForTest";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Provider(IServiceTokenProvider token, HttpMessageHandler primary)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(ClientName, c => c.BaseAddress = new Uri("http://llm-gateway:5007"))
            .AddLlmGatewayServiceToken(new ConfigurationBuilder().Build())
            .ConfigurePrimaryHttpMessageHandler(() => primary);
        services.RemoveAll<IServiceTokenProvider>();
        services.AddSingleton(token);
        return services.BuildServiceProvider();
    }

    private static HttpClient ClientOf(ServiceProvider sp) =>
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

    // 🔴 T-02: **呼び出し側サービス自身の s2s トークンが載る。**
    [Fact]
    public async Task It_attaches_the_service_token_to_every_request()
    {
        var spy = new SpyHandler();
        using var sp = Provider(new FixedToken("s2s-token"), spy);

        await ClientOf(sp).PostAsync("/complete", new StringContent("{}"), Ct);

        spy.LastAuthorization.Should().Be("Bearer s2s-token");
    }

    // 🔴 T-03: **トークンを取れないときは `HttpRequestException` へ畳む。**
    //
    // 呼び出し元は `HttpRequestException` / `TaskCanceledException` **だけ**を捕まえて縮退する。
    // 素の `InvalidOperationException` を通すと、**縮退の枝を素通りして呼び出し元の要求が失敗する** ——
    // 「後段へ届かない」が縮退ではなく**障害**に化ける。
    [Fact]
    public async Task A_token_failure_degrades_through_the_existing_degrade_path()
    {
        var spy = new SpyHandler();
        using var sp = Provider(
            new ThrowingToken(new InvalidOperationException("ServiceToken:ClientId が未設定です。")), spy);

        var act = async () => await ClientOf(sp).PostAsync("/complete", new StringContent("{}"), Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
        spy.Called.Should().BeFalse("トークンが無いまま呼びに行かない");
    }

    // 🔴 T-04: **呼び出し元のキャンセルだけは伝播する**（deny へ畳まない）。
    // ［#1630］取り消しは **実物のトークン取得（`ClientCredentialsServiceTokenProvider`）が表す形** —— 受け取った token を持つ
    // `TaskCanceledException`（`SemaphoreSlim.WaitAsync` も HttpClient もこの型で表す）—— で起こす。素の `OperationCanceledException` を
    // 注入していた間は、絞り込みを「`TaskCanceledException` なら時間切れ（deny へ畳む）」と**型で**判定する変異
    // （`|| ex is TaskCanceledException`）が生き残った。
    // 🔴 型の表明（`OperationCanceledException`）だけではその変異を殺せない —— 変異が畳んだ `HttpRequestException` を、呼び出し元の
    // token が立っているので HttpClient が取り消しへ包み直す（実測）。**外へ出たのがトークン取得の取り消しそのもの（HttpClient が包む
    // なら、その内側）であること**で測る。
    [Fact]
    public async Task The_callers_cancellation_still_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var token = new CancelledToken();
        var spy = new SpyHandler();
        using var sp = Provider(token, spy);

        var act = async () => await ClientOf(sp).PostAsync("/complete", new StringContent("{}"), cts.Token);

        var thrown = (await act.Should().ThrowAsync<OperationCanceledException>()).Which;
        token.Thrown.Should().NotBeNull("前提: 取り消しはトークン取得まで届いている");
        (thrown == token.Thrown || thrown.InnerException == token.Thrown).Should().BeTrue(
            "トークン取得の取り消しを deny（HttpRequestException）へ畳まず、そのまま（HttpClient が包むなら内側に）伝えている");
        spy.Called.Should().BeFalse("取り消されたまま呼びに行かない");
    }

    private sealed class FixedToken(string token) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct) => ValueTask.FromResult(token);
    }

    private sealed class ThrowingToken(Exception ex) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct) => throw ex;
    }

    // 取り消された ct を受け取ったら、その ct を持つ `TaskCanceledException` を投げる（実物のトークン取得の形。#1630）。
    private sealed class CancelledToken : IServiceTokenProvider
    {
        public TaskCanceledException? Thrown { get; private set; }

        public ValueTask<string> GetTokenAsync(CancellationToken ct)
        {
            if (!ct.IsCancellationRequested)
                throw new InvalidOperationException("呼び出し元の取り消しが ct に届いていない（試験の前提の誤り）");
            throw Thrown = new TaskCanceledException("A task was canceled.", null, ct);
        }
    }

    private sealed class SpyHandler : HttpMessageHandler
    {
        public bool Called { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Called = true;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        }
    }
}
