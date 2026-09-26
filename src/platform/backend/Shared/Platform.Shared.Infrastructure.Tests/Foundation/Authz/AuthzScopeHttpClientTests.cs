using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Authz;

// FR-05, NFR-09, 計画 ADR-0029, ADR-0075, **ADR-0088 決定 2**, [[IADR-0379]] 決定 4,
// [[IADR-0412]], [[IADR-0413]] (#1333):
// ABAC スコープ解決専用クライアントの 3 つの不変条件を固定する。
//
// 1. **名簿の経路と別のクライアントである**（資格情報の意味論が逆なので混ぜてはならない）
// 2. **s2s トークンを付ける**（受け口が `ServiceCaller` を要るようになった）
// 3. **トークン取得失敗を `HttpRequestException` へ畳む**（呼び出し元の deny 縮退へ合流させる）
[Trait("TestKind", "Unit")]
public class AuthzScopeHttpClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Provider(IServiceTokenProvider token, HttpMessageHandler primary)
    {
        var services = new ServiceCollection();
        services.AddPlatformAuthzScopeHttpClient(new ConfigurationBuilder().Build());
        services.RemoveAll<IServiceTokenProvider>();
        services.AddSingleton(token);
        services.AddHttpClient(AuthzScopeHttpClient.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => primary);
        return services.BuildServiceProvider();
    }

    private static HttpClient ClientOf(ServiceProvider sp) =>
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(AuthzScopeHttpClient.ClientName);

    // 🔴 T-01: **名簿の経路と同じ名前を使わない。**
    // 名前つきクライアント `"AuthorizationService"` は `/authz/users` で**利用者の資格を転送**する。
    // 同じ名前へ s2s を付けると両者が混ざる —— PR #1332（[[IADR-0412]]）と同型の事故である。
    [Fact]
    public void The_scope_client_is_not_the_directory_client()
        => AuthzScopeHttpClient.ClientName.Should().NotBe("AuthorizationService",
            "資格情報の意味論が逆な 2 用途を 1 本のクライアントに載せない");

    // 陽性対照: 宛先の構成キーは**共有する**（宛先は同じサービスである）。
    // 🔴 1 つの宛先を 2 つの鍵で切り替えると、片方だけが別のホストを向く状態が作れる。
    [Fact]
    public void The_scope_client_points_at_the_same_service_as_the_directory_client()
        => AuthzScopeHttpClient.AddressKey.Should().Be("Services:AuthorizationService");

    // 🔴 T-02: **呼び出し側サービス自身の s2s トークンが載る。**
    [Fact]
    public async Task It_attaches_the_service_token_to_every_request()
    {
        var spy = new SpyHandler();
        using var sp = Provider(new FixedToken("s2s-token"), spy);

        await ClientOf(sp).PostAsync("/authz/scope", new StringContent("{}"), Ct);

        spy.LastAuthorization.Should().Be("Bearer s2s-token");
    }

    // 🔴 T-03: **トークンを取れないときは `HttpRequestException` へ畳む。**
    //
    // 呼び出し元 4 つ（`BffScopeResolver` / `RagOrchestrator` / `GraphAccessResolver` /
    // `WikiAccessResolver`）はいずれも `HttpRequestException` / `TaskCanceledException` **だけ**を
    // 捕まえて deny へ縮退する。素の `InvalidOperationException` を通すと、
    // **その 4 つを素通りして呼び出し元の要求が 500 になる** ——
    // 「認可サービスへ届かない」が deny ではなく**障害**に化ける。
    [Fact]
    public async Task A_token_failure_degrades_through_the_existing_deny_path()
    {
        var spy = new SpyHandler();
        using var sp = Provider(
            new ThrowingToken(new InvalidOperationException("ServiceToken:ClientId が未設定です。")), spy);

        var act = async () => await ClientOf(sp).PostAsync("/authz/scope", new StringContent("{}"), Ct);

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

        var act = async () => await ClientOf(sp).PostAsync("/authz/scope", new StringContent("{}"), cts.Token);

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
