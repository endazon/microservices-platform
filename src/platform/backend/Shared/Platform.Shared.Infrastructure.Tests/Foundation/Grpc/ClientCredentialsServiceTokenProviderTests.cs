using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Grpc;

// NFR-09, ADR-0004, IADR-0379 決定 4 (#1201): s2s トークン（client credentials）の取得・再利用・失敗の扱いを固定する。
//
// - 期限内は IdP を叩き直さない（毎回叩くと IdP が east-west の律速になる）。
// - 期限の RefreshSkewSeconds 手前で取り直す（時計のずれ・往復時間）。
// - 取得失敗は例外 —— 呼び出し側が deny-by-default へ縮退させる材料になる。匿名で呼ぶ形にしない。
public class ClientCredentialsServiceTokenProviderTests
{
    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastBody { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage TokenJson(string token, int expiresIn) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { access_token = token, expires_in = expiresIn, token_type = "Bearer" }),
            Encoding.UTF8, "application/json"),
    };

    private static ClientCredentialsServiceTokenProvider Build(
        HttpMessageHandler handler, TimeProvider time, ServiceTokenOptions? opts = null, string? authority = "http://keycloak:8080/realms/platform")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Authority"] = authority,
        }).Build();
        opts ??= new ServiceTokenOptions { ClientId = "bff", ClientSecret = "secret" };
        return new ClientCredentialsServiceTokenProvider(
            new StubFactory(handler), Options.Create(opts), config, time,
            NullLogger<ClientCredentialsServiceTokenProvider>.Instance);
    }

    [Fact]
    public async Task Posts_client_credentials_to_the_realm_token_endpoint()
    {
        var handler = new StubHandler(_ => TokenJson("tok-1", 300));
        var provider = Build(handler, new ManualTimeProvider(DateTimeOffset.UnixEpoch));

        var token = await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        token.Should().Be("tok-1");
        handler.LastUri!.ToString().Should().Be("http://keycloak:8080/realms/platform/protocol/openid-connect/token");
        handler.LastBody.Should().Contain("grant_type=client_credentials")
            .And.Contain("client_id=bff").And.Contain("client_secret=secret");
    }

    [Fact]
    public async Task Reuses_the_token_until_the_refresh_point_then_fetches_again()
    {
        var handler = new StubHandler(_ => TokenJson($"tok-{Guid.NewGuid():N}", 300));
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var provider = Build(handler, time, new ServiceTokenOptions { ClientId = "bff", ClientSecret = "s", RefreshSkewSeconds = 30 });

        var first = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        time.Now = time.Now.AddSeconds(269); // 300 - 30 = 270 秒より手前
        var second = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        second.Should().Be(first);
        handler.Calls.Should().Be(1);

        time.Now = time.Now.AddSeconds(2); // 271 秒 → 取り直し
        var third = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        third.Should().NotBe(first);
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Non_success_from_the_idp_throws_instead_of_returning_an_empty_token()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var provider = Build(handler, new ManualTimeProvider(DateTimeOffset.UnixEpoch));

        var act = async () => await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*401*");
    }

    [Fact]
    public async Task Missing_client_credentials_throws_before_calling_the_idp()
    {
        var handler = new StubHandler(_ => TokenJson("never", 300));
        var provider = Build(handler, new ManualTimeProvider(DateTimeOffset.UnixEpoch), new ServiceTokenOptions());

        var act = async () => await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ClientId*");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public void Explicit_token_endpoint_wins_over_authority_and_both_missing_throws()
    {
        var empty = new ConfigurationBuilder().Build();
        ClientCredentialsServiceTokenProvider.ResolveTokenEndpoint(
                new ServiceTokenOptions { TokenEndpoint = "http://idp/token" }, empty)
            .Should().Be("http://idp/token");

        var act = () => ClientCredentialsServiceTokenProvider.ResolveTokenEndpoint(new ServiceTokenOptions(), empty);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:Authority*");
    }
}
