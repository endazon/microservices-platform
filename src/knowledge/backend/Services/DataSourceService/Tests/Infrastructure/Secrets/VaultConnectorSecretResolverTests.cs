using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using DataSourceService.Domain.Ports;
using DataSourceService.Infrastructure.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DataSourceService.Tests.Infrastructure.Secrets;

// FR-01, UC-04, NFR-18, [[IADR-0495]] 決定 1・2・4 (#458 段 S1): Vault 解決器。
// HTTP は偽のハンドラで受け、k8s auth のログインと KV v2 の GET だけを話すこと、失敗を既存の符号へ写すこと、
// 平文へ倒さないこと、ログに値・パス・キー名・例外文を出さないことを固定する。
[Trait("TestKind", "Unit")]
public sealed class VaultConnectorSecretResolverTests
{
    // 値・パスの断片は、漏洩の検査で探す目印として一意な語にしておく。
    private const string SecretValue = "resolved-credential-marker-7f3a";
    private const string PathMarker = "ds-path-marker-91c2";
    private const string KeyMarker = "keyMarker42";
    private const string Reference = "vault:datasource/" + PathMarker + "#" + KeyMarker;

    // 名乗りの資格情報（SA の JWT・Vault のトークン）も漏洩の目印にする（監査 🟡-2）。
    private const string JwtMarker = "sa-jwt-marker-5d1e";
    private const string TokenPrefix = "hvs.vault-token-marker-";

    // 監査 🟡-1: KV v2 の読み取りの**実応答の形**（現在版が生きているとき deletion_time は空文字、destroyed は false）。
    private static readonly object LiveMetadata = new
    {
        created_time = "2026-10-01T00:00:00.000000Z",
        custom_metadata = (object?)null,
        deletion_time = "",
        destroyed = false,
        version = 3,
    };

    private static string KvBody(object? data, object? metadata = null) =>
        JsonSerializer.Serialize(new { data = new { data, metadata = metadata ?? LiveMetadata } });

    // ログに値・参照のパス・キー名・SA の JWT・Vault のトークンのいずれも出ない。
    private static void AssertNoLeak(StubVault vault)
    {
        vault.LogText.Should().NotContain(SecretValue).And.NotContain(PathMarker).And.NotContain(KeyMarker)
            .And.NotContain(JwtMarker).And.NotContain(TokenPrefix);
        vault.Logs.Should().OnlyContain(l => l.Exception == null);
    }

    [Fact]
    public async Task Reference_IsReadFromTheKvV2DataPath_WithTheLoginToken()
    {
        var vault = new StubVault { Data = _ => (HttpStatusCode.OK, KvBody(new Dictionary<string, string> { [KeyMarker] = SecretValue })) };
        var resolver = vault.CreateResolver();

        var result = await resolver.ResolveAsync(Reference, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Value.Should().Be(SecretValue);
        vault.DataRequests.Should().ContainSingle().Which.Should().Be($"/v1/secret/data/datasource/{PathMarker}");
        vault.DataTokens.Should().Equal([TokenPrefix + "1"]);
        vault.LoginBodies.Should().ContainSingle();
        using var login = JsonDocument.Parse(vault.LoginBodies[0]);
        login.RootElement.GetProperty("role").GetString().Should().Be("datasource-connector-reader");
        login.RootElement.GetProperty("jwt").GetString().Should().Be(JwtMarker);
        vault.LoginPaths.Should().Equal(["/v1/auth/kubernetes/login"]);
    }

    // 移送期間（[[IADR-0493]] 決定 2）: 平文は素通し。Vault へは何も送らない。
    [Fact]
    public async Task Plaintext_PassesThrough_WithoutTouchingVault()
    {
        var vault = new StubVault();
        var result = await vault.CreateResolver().ResolveAsync("plain-config-value", CancellationToken.None);

        result.Value.Should().Be("plain-config-value");
        vault.TotalRequests.Should().Be(0);
    }

    // 専用接頭辞の外・形の誤り・パスの上り（`..`）は Vault へ送らずに止める。
    [Theory]
    [InlineData("vault:datasource/ds-1")]
    [InlineData("vault:msp/postgres#password")]
    [InlineData("vault:ai-stock-trading/app-secrets#finnhub-api-key")]
    [InlineData("vault:Datasource/ds-1#apiToken")]
    [InlineData("vault:datasource/#apiToken")]
    [InlineData("vault:datasource#apiToken")]
    [InlineData("vault:datasource/../msp/postgres#password")]
    [InlineData("vault:datasource/./ds-1#apiToken")]
    [InlineData("vault:datasource//ds-1#apiToken")]
    [InlineData("vault:/datasource/ds-1#apiToken")]
    public async Task ReferenceOutsideTheDedicatedPrefixOrMalformed_IsRejected_WithoutAnyRequest(string value)
    {
        var vault = new StubVault();
        var result = await vault.CreateResolver().ResolveAsync(value, CancellationToken.None);

        result.Failure.Should().Be(ConnectorSecretFailure.MalformedReference);
        vault.TotalRequests.Should().Be(0);
    }

    [Theory]
    [InlineData("vault:datasource/ds-1#apiToken")]
    [InlineData("VAULT:datasource/a/b-c_1#password")]
    public void DedicatedPrefix_AcceptsNestedPaths(string value)
    {
        DataSourceService.Domain.ConnectorSecretReference.TryParse(value, out var reference).Should().BeTrue();
        VaultConnectorSecretResolver.IsUnderDedicatedPrefix(reference!.Path).Should().BeTrue();
    }

    public static TheoryData<HttpStatusCode, string, ConnectorSecretFailure> FailureResponses => new()
    {
        // パス無し・現在版の削除・破棄（Vault は 404 に metadata を付けて返す）
        { HttpStatusCode.NotFound, "{\"errors\":[]}", ConnectorSecretFailure.NotFound },
        { HttpStatusCode.NotFound, KvBody(null, new { created_time = "2026-10-01T00:00:00.000000Z", version = 2, deletion_time = "2026-10-03T00:00:00Z", destroyed = false }), ConnectorSecretFailure.NotFound },
        { HttpStatusCode.NotFound, KvBody(null, new { created_time = "2026-10-01T00:00:00.000000Z", version = 2, deletion_time = "", destroyed = true }), ConnectorSecretFailure.NotFound },
        // 200 で返っても、現在版が削除・破棄されていれば値として扱わない
        { HttpStatusCode.OK, KvBody(new Dictionary<string, string> { [KeyMarker] = SecretValue }, new { created_time = "2026-10-01T00:00:00.000000Z", version = 2, deletion_time = "2026-10-03T00:00:00Z", destroyed = false }), ConnectorSecretFailure.NotFound },
        { HttpStatusCode.OK, KvBody(new Dictionary<string, string> { [KeyMarker] = SecretValue }, new { created_time = "2026-10-01T00:00:00.000000Z", version = 2, deletion_time = "", destroyed = true }), ConnectorSecretFailure.NotFound },
        { HttpStatusCode.OK, KvBody(null), ConnectorSecretFailure.NotFound },
        // キー無し・値が文字列でない
        { HttpStatusCode.OK, KvBody(new Dictionary<string, string> { ["other"] = SecretValue }), ConnectorSecretFailure.NotFound },
        { HttpStatusCode.OK, KvBody(new Dictionary<string, object> { [KeyMarker] = 42 }), ConnectorSecretFailure.NotFound },
        // 値が空白
        { HttpStatusCode.OK, KvBody(new Dictionary<string, string> { [KeyMarker] = "  " }), ConnectorSecretFailure.Empty },
        // サーバ側の失敗・応答の解釈不能
        { HttpStatusCode.InternalServerError, "{}", ConnectorSecretFailure.Unreachable },
        { HttpStatusCode.ServiceUnavailable, "{}", ConnectorSecretFailure.Unreachable },
        { HttpStatusCode.OK, "not-json " + SecretValue, ConnectorSecretFailure.Unreachable },
        { HttpStatusCode.OK, "[]", ConnectorSecretFailure.Unreachable },
    };

    [Theory]
    [MemberData(nameof(FailureResponses))]
    public async Task VaultResponses_MapToTheExistingFailureCodes_AndNeverFallBackToPlaintext(
        HttpStatusCode status, string body, ConnectorSecretFailure expected)
    {
        var vault = new StubVault { Data = _ => (status, body) };
        var result = await vault.CreateResolver().ResolveAsync(Reference, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(expected);
        result.Value.Should().BeNull();
        AssertNoLeak(vault);
    }

    // 403 はトークンを取り直して 1 度だけやり直す。2 度目も 403 なら Unreachable（権限が無い）。
    [Fact]
    public async Task Forbidden_RetriesOnceWithAFreshToken_ThenFailsClosed()
    {
        var vault = new StubVault { Data = _ => (HttpStatusCode.Forbidden, "{\"errors\":[\"permission denied\"]}") };
        var result = await vault.CreateResolver().ResolveAsync(Reference, CancellationToken.None);

        result.Failure.Should().Be(ConnectorSecretFailure.Unreachable);
        vault.LoginBodies.Should().HaveCount(2);
        vault.DataTokens.Should().Equal([TokenPrefix + "1", TokenPrefix + "2"]);
        vault.Logs.Should().NotBeEmpty();
        AssertNoLeak(vault);
    }

    // 403 以外の失敗（404・5xx）ではトークンを取り直さない（ログインは 1 回）。
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NonForbiddenFailure_DoesNotRelogin(HttpStatusCode status)
    {
        var vault = new StubVault { Data = _ => (status, "{}") };
        await vault.CreateResolver().ResolveAsync(Reference, CancellationToken.None);

        vault.LoginBodies.Should().HaveCount(1);
        vault.DataRequests.Should().HaveCount(1);
        AssertNoLeak(vault);
    }

    [Fact]
    public async Task Forbidden_ThenSuccess_AfterTokenRefresh_Resolves()
    {
        var vault = new StubVault
        {
            Data = token => token == TokenPrefix + "1"
                ? (HttpStatusCode.Forbidden, "{}")
                : (HttpStatusCode.OK, KvBody(new Dictionary<string, string> { [KeyMarker] = SecretValue })),
        };
        var result = await vault.CreateResolver().ResolveAsync(Reference, CancellationToken.None);

        result.Value.Should().Be(SecretValue);
        AssertNoLeak(vault);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task LoginFailure_FailsClosed_WithoutReadingData(HttpStatusCode loginStatus)
    {
        var vault = new StubVault { LoginStatus = loginStatus };
        var result = await vault.CreateResolver().ResolveAsync(Reference, CancellationToken.None);

        result.Failure.Should().Be(ConnectorSecretFailure.Unreachable);
        vault.DataRequests.Should().BeEmpty();
        vault.Logs.Should().NotBeEmpty();
        AssertNoLeak(vault);
    }

    [Fact]
    public async Task UnreadableServiceAccountToken_FailsClosed_WithoutAnyRequest()
    {
        var vault = new StubVault { TokenReader = new ThrowingTokenReader() };
        var result = await vault.CreateResolver().ResolveAsync(Reference, CancellationToken.None);

        result.Failure.Should().Be(ConnectorSecretFailure.Unreachable);
        vault.TotalRequests.Should().Be(0);
    }

    // 不達・時間切れ。🔴 例外文に値とパスを入れた陽性対照で、ログへ例外文も例外オブジェクトも渡らないことを測る。
    public static TheoryData<string> TransportFailures => new() { "unreachable", "timeout", "login-unreachable" };

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task TransportFailure_FailsClosed_AndLeaksNothing(string kind)
    {
        var leakyMessage = $"connect failed for /v1/secret/data/datasource/{PathMarker} {KeyMarker}={SecretValue}";
        var vault = new StubVault
        {
            Throw = (isLogin) => kind switch
            {
                "unreachable" when !isLogin => new HttpRequestException(leakyMessage),
                "timeout" when !isLogin => new TaskCanceledException(leakyMessage),
                "login-unreachable" when isLogin => new HttpRequestException(leakyMessage),
                _ => null,
            },
            Data = _ => (HttpStatusCode.OK, KvBody(new Dictionary<string, string> { [KeyMarker] = SecretValue })),
        };
        var result = await vault.CreateResolver().ResolveAsync(Reference, CancellationToken.None);

        result.Failure.Should().Be(ConnectorSecretFailure.Unreachable);
        vault.Logs.Should().NotBeEmpty("切り分けの手掛かり（型名）は残すこと");
        AssertNoLeak(vault);
        vault.LogText.Should().NotContain("connect failed");
    }

    // 呼び出し側の取り消しだけは外へ出す（[[IADR-0493]] 決定 3）。
    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var vault = new StubVault
        {
            Data = _ =>
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
                return (HttpStatusCode.OK, "{}");
            },
        };
        var act = () => vault.CreateResolver().ResolveAsync(Reference, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // トークンはリースの間は使い回し、満了の手前で取り直す。
    [Fact]
    public async Task Token_IsReusedWithinTheLease_AndRenewedBeforeExpiry()
    {
        var vault = new StubVault { Data = _ => (HttpStatusCode.OK, KvBody(new Dictionary<string, string> { [KeyMarker] = SecretValue })) };
        var resolver = vault.CreateResolver();

        await resolver.ResolveAsync(Reference, CancellationToken.None);
        await resolver.ResolveAsync(Reference, CancellationToken.None);
        vault.LoginBodies.Should().HaveCount(1);

        vault.Clock.Advance(TimeSpan.FromSeconds(3600 - 29));
        await resolver.ResolveAsync(Reference, CancellationToken.None);
        vault.LoginBodies.Should().HaveCount(2);
        vault.DataTokens.Should().Equal([TokenPrefix + "1", TokenPrefix + "1", TokenPrefix + "2"]);
    }

    // 成功時もログに値・パス・キー名を出さない。
    [Fact]
    public async Task Success_LogsNothingSensitive()
    {
        var vault = new StubVault { Data = _ => (HttpStatusCode.OK, KvBody(new Dictionary<string, string> { [KeyMarker] = SecretValue })) };
        var result = await vault.CreateResolver().ResolveAsync(Reference, CancellationToken.None);

        result.ToString().Should().NotContain(SecretValue);
        AssertNoLeak(vault);
    }

    // リースが短い（≤ 60 秒）ときは満了の手前で削らず、リースいっぱいまで使う（削ると 0 秒以下になり毎回ログインする）。
    [Fact]
    public async Task ShortLease_IsUsedInFull_NotShortenedByTheRenewMargin()
    {
        var vault = new StubVault
        {
            LeaseSeconds = 60,
            Data = _ => (HttpStatusCode.OK, KvBody(new Dictionary<string, string> { [KeyMarker] = SecretValue })),
        };
        var resolver = vault.CreateResolver();

        await resolver.ResolveAsync(Reference, CancellationToken.None);
        vault.Clock.Advance(TimeSpan.FromSeconds(59));
        await resolver.ResolveAsync(Reference, CancellationToken.None);
        vault.LoginBodies.Should().HaveCount(1, "60 秒のリースは 59 秒後もまだ有効");

        vault.Clock.Advance(TimeSpan.FromSeconds(1));
        await resolver.ResolveAsync(Reference, CancellationToken.None);
        vault.LoginBodies.Should().HaveCount(2, "満了したら取り直す");
    }

    // 監査 🟡-3: 接頭辞の判定を通る参照でも、パスのセグメントは URL の符号化を経て送られる
    // （`%2e%2e` が `..` へ戻って接頭辞の外へ出ない・`?` がクエリにならない）。
    [Theory]
    [InlineData("vault:datasource/%2e%2e/msp/postgres#password", "http://vault.test:8200/v1/secret/data/datasource/%252e%252e/msp/postgres")]
    [InlineData("vault:datasource/..%2fmsp%2fpostgres#password", "http://vault.test:8200/v1/secret/data/datasource/..%252fmsp%252fpostgres")]
    [InlineData("vault:datasource/%2E%2E#password", "http://vault.test:8200/v1/secret/data/datasource/%252E%252E")]
    [InlineData(@"vault:datasource/\..\msp#password", "http://vault.test:8200/v1/secret/data/datasource/%5C..%5Cmsp")]
    [InlineData("vault:datasource/ds-1?x=1#apiToken", "http://vault.test:8200/v1/secret/data/datasource/ds-1%3Fx%3D1")]
    public async Task PathSegments_AreUrlEncoded_AndStayUnderTheDedicatedPrefix(string reference, string expectedUri)
    {
        var vault = new StubVault { Data = _ => (HttpStatusCode.NotFound, "{}") };
        await vault.CreateResolver().ResolveAsync(reference, CancellationToken.None);

        var sent = vault.DataUris.Should().ContainSingle().Subject;
        sent.Should().StartWith("http://vault.test:8200/v1/secret/data/datasource/");
        sent.Should().Be(expectedUri);
        if (reference.Contains('%'))
            sent.Should().Contain("%25", "入力の % は % のまま送らず %25 へ符号化すること");
    }

    // `Vault:Address` は起動時に検証する: 空（Vault なし）か、絶対の http / https の URI だけ。
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("http://vault.platform-infra.svc.cluster.local:8200", true)]
    [InlineData("https://vault.example.test", true)]
    [InlineData("vault.platform-infra.svc.cluster.local:8200", false)]
    [InlineData("/v1/vault", false)]
    [InlineData("ftp://vault.example.test", false)]
    [InlineData("file:///var/run/vault", false)]
    public void VaultAddress_IsValidatedOnStart(string? address, bool valid)
    {
        var settings = new Dictionary<string, string?>();
        if (address is not null) settings["Vault:Address"] = address;
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddConnectorSecretResolver();
        using var provider = services.BuildServiceProvider();

        var validate = () => provider.GetRequiredService<IStartupValidator>().Validate();
        if (valid)
            validate.Should().NotThrow();
        else
            validate.Should().Throw<OptionsValidationException>();
    }

    // 配線（[[IADR-0495]] 決定 3）: Vault が未構成なら S0 の素通し、構成済みなら Vault 解決器。
    [Theory]
    [InlineData(null, typeof(PlaintextPassthroughConnectorSecretResolver))]
    [InlineData("", typeof(PlaintextPassthroughConnectorSecretResolver))]
    [InlineData("   ", typeof(PlaintextPassthroughConnectorSecretResolver))]
    [InlineData("http://vault.platform-infra.svc.cluster.local:8200", typeof(VaultConnectorSecretResolver))]
    public void Wiring_ChoosesTheResolverByVaultAddress(string? address, Type expected)
    {
        var settings = new Dictionary<string, string?>();
        if (address is not null) settings["Vault:Address"] = address;
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddConnectorSecretResolver();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IConnectorSecretResolver>().Should().BeOfType(expected);
    }

    [Fact]
    public async Task Wiring_WithoutVault_KeepsReferencesFailClosedAsResolverUnavailable()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddConnectorSecretResolver();
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IConnectorSecretResolver>().ResolveAsync(Reference, CancellationToken.None);
        result.Failure.Should().Be(ConnectorSecretFailure.ResolverUnavailable);
    }

    // ------------------------------------------------------------------------------------------------

    private sealed class StubVault : HttpMessageHandler
    {
        private int _logins;

        public Func<string, (HttpStatusCode Status, string Body)> Data { get; set; } = _ => (HttpStatusCode.NotFound, "{}");
        public HttpStatusCode LoginStatus { get; set; } = HttpStatusCode.OK;
        public Func<bool, Exception?> Throw { get; set; } = _ => null;
        public IServiceAccountTokenReader TokenReader { get; set; } = new FixedTokenReader();
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-10-03T00:00:00Z"));

        public List<string> LoginPaths { get; } = [];
        public List<string> LoginBodies { get; } = [];
        public List<string> DataRequests { get; } = [];
        public List<string> DataUris { get; } = [];
        public int LeaseSeconds { get; set; } = 3600;
        public List<string> DataTokens { get; } = [];
        public int TotalRequests => LoginPaths.Count + DataRequests.Count;
        public List<(string Message, Exception? Exception)> Logs { get; } = [];
        public string LogText => string.Join('\n', Logs.Select(l => l.Message));

        public VaultConnectorSecretResolver CreateResolver()
        {
            var options = Options.Create(new VaultConnectorSecretOptions { Address = "http://vault.test:8200" });
            return new VaultConnectorSecretResolver(
                new StubFactory(this), options, TokenReader, Clock, new RecordingLogger(Logs));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var isLogin = path.Contains("/auth/", StringComparison.Ordinal);
            if (isLogin)
            {
                LoginPaths.Add(path);
                LoginBodies.Add(await request.Content!.ReadAsStringAsync(ct));
            }
            else
            {
                DataRequests.Add(path);
                DataUris.Add(request.RequestUri.AbsoluteUri);
                DataTokens.Add(request.Headers.GetValues("X-Vault-Token").Single());
            }

            if (Throw(isLogin) is { } ex) throw ex;

            if (isLogin)
            {
                if (LoginStatus != HttpStatusCode.OK)
                    return new HttpResponseMessage(LoginStatus) { Content = new StringContent("{}") };
                _logins++;
                var body = JsonSerializer.Serialize(new { auth = new { client_token = $"{TokenPrefix}{_logins}", lease_duration = LeaseSeconds } });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }

            var (status, payload) = Data(DataTokens[^1]);
            return new HttpResponseMessage(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://vault.test:8200/") };
    }

    private sealed class FixedTokenReader : IServiceAccountTokenReader
    {
        public Task<string> ReadAsync(CancellationToken ct) => Task.FromResult(JwtMarker);
    }

    private sealed class ThrowingTokenReader : IServiceAccountTokenReader
    {
        public Task<string> ReadAsync(CancellationToken ct) => throw new IOException("token file missing");
    }

    private sealed class RecordingLogger(List<(string Message, Exception? Exception)> records) : ILogger<VaultConnectorSecretResolver>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => records.Add((formatter(state, exception), exception));
    }
}
