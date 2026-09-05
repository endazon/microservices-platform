using System.Runtime.CompilerServices;
using System.Text;
using Anthropic.SDK;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Tests.Grpc;

// FR-02, FR-03, FR-05, NFR-09, NFR-16, ADR-0016, ADR-0029, ADR-0075, IADR-0379, IADR-0397 (#1255):
// 埋め込みの gRPC 面の器。AuthorizationService の参照実装（#1201）と同型である。
//
// 🔴 **TestServer ではなく実 Kestrel で起こす。** TestServer は in-memory であり、h2c（TLS 無し HTTP/2）の
// ポートが実際に bind され、HTTP/1.1 のポートが**消えていない**ことを観測できない
// （AddPlatformGrpcListener の 🔴 を参照）。h2c 用ポートは GrpcTestConfiguration が環境変数で与える。
//
// 認証は実 IdP を持たないので JwtBearer の検証鍵と issuer をテスト用の対称鍵へ差し替える。
// TestAuthHandler は使わない —— s2s の検証は**本物の JwtBearer パイプライン**
// （AddPlatformAuth ＋ KeycloakRolesClaimsTransformation）を通してこそ意味がある。
//
// 埋め込みプロバイダは外部 API を持たないスタブへ差し替える（TestWebApplicationFactory と同じ形）。
// **ルータ（EmbeddingRouter / LlmRouter）と越境判定は差し替えない** —— そこが本試験の観測対象だからである。
//
// IADR-0398 (#1255): テキスト生成の gRPC 面（LlmCompletion）が加わったため、**本器は 2 つの面を
// 同じ 1 プロセスで供する**。器を 2 つにすると、`GrpcTestConfiguration` がプロセスで 1 つだけ選ぶ
// h2c ポートを 2 つの Kestrel が奪い合って bind に失敗する ——
// だから各テストクラスの `IClassFixture` ではなく **`GrpcServerCollection` の共有器**にしてある。
public sealed class GrpcKestrelFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://test-issuer/realms/platform";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("grpc-s2s-test-signing-key-0123456789abcdef-0123456789"));

    // ポートは GrpcTestConfiguration（環境変数）が決める。ConfigureAppConfiguration では間に合わない。
    public static int GrpcPort => GrpcTestConfiguration.GrpcPort;

    public GrpcKestrelFactory()
    {
        UseKestrel();
    }

    public string GrpcAddress => $"http://127.0.0.1:{GrpcPort}";

    // HTTP/1.1 側（REST・/health/*）の実アドレス。gRPC を有効にしてもこちらが残っていることの証明に使う。
    public string HttpAddress
    {
        get
        {
            StartServer();
            var addresses = Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()?.Addresses ?? [];
            return addresses.First(a => !a.EndsWith($":{GrpcPort}", StringComparison.Ordinal))
                .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:ApiKey"] = "test-key",
                ["Llm:Model"] = "claude-opus-5",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = Issuer,
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AnthropicClient>();

            // IADR-0398 (#1255): テキスト生成プロバイダを**台本つきスタブ**へ差し替える。
            // 台本はプロンプト中の標識で決まる（ScriptedLlmProvider を参照）—— 共有器で並列に
            // 走る試験どうしが可変状態を取り合わないようにするためである。
            // **ルータ（LlmRouter）と越境判定は差し替えない**（本試験の観測対象）。
            services.RemoveAll<ILlmProvider>();
            var llm = new ScriptedLlmProvider();
            services.AddKeyedSingleton<ILlmProvider>("claude", llm);
            services.AddKeyedSingleton<ILlmProvider>("selfhosted", llm);
            services.AddKeyedSingleton<ILlmProvider>("copilot", llm);

            // 外部 API 基盤を持たないので埋め込みプロバイダだけスタブへ差し替える
            // （要求次元どおりのベクトルを返す）。ルータと越境判定は本物のままである。
            services.RemoveAll<IEmbeddingProvider>();
            services.AddKeyedSingleton<IEmbeddingProvider, StubEmbeddingProvider>("voyage");
            services.AddKeyedSingleton<IEmbeddingProvider, StubEmbeddingProvider>("selfhosted-embedding");
            services.AddKeyedSingleton<IEmbeddingProvider, StubEmbeddingProvider>("deterministic-embedding");

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                    new OpenIdConnectConfiguration { Issuer = Issuer });
                o.TokenValidationParameters.IssuerSigningKey = SigningKey;
                o.TokenValidationParameters.ValidIssuer = Issuer;
            });
        });
    }

    // テスト用 IdP の代わりに JWT を発行する。realm_access.roles は KeycloakRolesClaimsTransformation が
    // ClaimTypes.Role へ展開する（実 Keycloak トークンと同じ形）。
    public static string IssueToken(string subject, IEnumerable<string> realmRoles)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["preferred_username"] = subject,
                ["realm_access"] = new Dictionary<string, object> { ["roles"] = realmRoles.ToArray() },
            },
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

// IADR-0398 (#1255): テキスト生成の台本つきスタブ。
//
// 🔴 **台本はプロンプトの標識で決まる。可変状態を持たない。**
// 共有器（GrpcServerCollection）の下では複数のテストクラスが同じインスタンスを同時に使うため、
// 「テストが事前に振る舞いを仕込む」形にすると取り合いになって偽陽性・偽陰性が出る。
//
// 🔴 **受け取った max_tokens とモデルを応答本文へ書く。** これが決定 4 の写し
// （proto3 の `max_tokens=0` → REST 既定 4096）を**応答から観測できる**唯一の点である ——
// 写し漏れは例外にならないので、プロバイダが何を受け取ったかを線の上に出しておく。
internal sealed class ScriptedLlmProvider : ILlmProvider
{
    /// <summary>この標識を含むプロンプトは「モデルが拒否した」応答になる（stop_reason=refusal・本文空）。</summary>
    public const string RefusalMarker = "[[refusal]]";

    /// <summary>この標識を含むプロンプトは、2 つ目の delta と done を <see cref="StreamGap"/> だけ遅らせる。</summary>
    public const string SlowStreamMarker = "[[slow-stream]]";

    /// <summary>
    /// この標識を含むプロンプトは上流の失敗を模す（プロバイダが例外を投げる）。
    /// 🔴 これがゲートウェイの縮退（sent=false の応答／done メッセージ）を**既定構成で**踏める唯一の道である ——
    /// 既定の越境マトリクスはティアB が有効なので、restricted でも fail-closed にならない（実測）。
    /// フォールバック鎖は HTTP 400 系のときだけ発火する（ADR-0038 決定 4）ので、この例外では発火しない。
    /// </summary>
    public const string UpstreamFailureMarker = "[[upstream-fail]]";

    /// <summary>逐次生成の 1 つ目と 2 つ目の delta の間隔（初回トークンの境界を観測するための時間差）。</summary>
    public static readonly TimeSpan StreamGap = TimeSpan.FromMilliseconds(800);

    public const int InputTokens = 11;
    public const int OutputTokens = 22;

    // 🔴 受け取った引数を線の上へ出す（下の 🔴）。書式は試験が読む契約なので変えない。
    public static string Echo(CompletionRequest request) =>
        $"max_tokens={request.MaxTokens};model={request.Model ?? "(null)"}";

    public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        if (request.Prompt.Contains(UpstreamFailureMarker, StringComparison.Ordinal))
            throw new InvalidOperationException("scripted upstream failure");

        if (request.Prompt.Contains(RefusalMarker, StringComparison.Ordinal))
            return Task.FromResult(new CompletionResult(
                string.Empty, InputTokens, OutputTokens, CompletionStopReasons.Refusal));

        return Task.FromResult(new CompletionResult(
            Echo(request), InputTokens, OutputTokens, CompletionStopReasons.EndTurn));
    }

    public async IAsyncEnumerable<CompletionChunk> StreamAsync(
        CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (request.Prompt.Contains(UpstreamFailureMarker, StringComparison.Ordinal))
            throw new InvalidOperationException("scripted upstream failure");

        var slow = request.Prompt.Contains(SlowStreamMarker, StringComparison.Ordinal);
        var refusal = request.Prompt.Contains(RefusalMarker, StringComparison.Ordinal);

        // 1 つ目の delta は**即座に**出す。ここが「初回トークン」の位置である。
        yield return new CompletionChunk("first:" + Echo(request));

        if (slow)
            await Task.Delay(StreamGap, ct);

        yield return new CompletionChunk("|second");

        yield return new CompletionChunk(
            string.Empty, Done: true, InputTokens, OutputTokens,
            refusal ? CompletionStopReasons.Refusal : CompletionStopReasons.EndTurn);
    }
}

// 要求次元どおりの「本物らしい」ベクトルを返すスタブ（ゼロベクトルだと次元照合しか観測できない）。
file sealed class StubEmbeddingProvider : IEmbeddingProvider
{
    public Task<float[]> EmbedAsync(
        string text, string model, int dimensions, EmbeddingRoutePurpose purpose, CancellationToken ct = default)
    {
        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
            vector[i] = (i + 1) * 0.001f;
        return Task.FromResult(vector);
    }
}
