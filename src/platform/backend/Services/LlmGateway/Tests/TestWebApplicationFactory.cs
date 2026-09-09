using System.Net.Http.Headers;
using Anthropic.SDK;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace LlmGateway.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:ApiKey"] = "test-key",
                ["Llm:Model"] = "claude-opus-5",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                // #1364: issuer は発行器と揃える（`TestServiceTokens` が検証側も同じ値へ差し替える）。
                ["Auth:Authority"] = TestServiceTokens.Issuer,
            }));
        builder.ConfigureServices(services =>
        {
            // API キーなしで動くようにスタブ LLM プロバイダーへ差し替え。
            // FR-11: ルーターはキー付きプロバイダ（claude/selfhosted）を解決するため、キー付きでも差し替える。
            services.RemoveAll<AnthropicClient>();
            services.RemoveAll<ILlmProvider>();
            services.AddKeyedSingleton<ILlmProvider, StubLlmProvider>("claude");   // ティアB
            services.AddKeyedSingleton<ILlmProvider, StubLlmProvider>("selfhosted"); // ティアA
            services.AddKeyedSingleton<ILlmProvider, StubLlmProvider>("copilot");  // 最難関別経路（既定は無効エンドポイント）

            // FR-02: 埋め込みも API 基盤なしで動くようスタブへ差し替える。要求次元どおりのベクトルを返す。
            services.RemoveAll<IEmbeddingProvider>();
            services.AddKeyedSingleton<IEmbeddingProvider, StubEmbeddingProvider>("voyage");
            services.AddKeyedSingleton<IEmbeddingProvider, StubEmbeddingProvider>("selfhosted-embedding");
            // #992, [[IADR-0313]]: 決定的ローカル埋め込みは**外部依存が無い**（プロセス内計算）ので
            // スタブへ差し替えない。差し替えると「本物が動くこと」をここでは一切確かめられなくなる。
            services.AddKeyedSingleton<IEmbeddingProvider, DeterministicEmbeddingProvider>("deterministic-embedding");

            // NFR-09, ADR-0084 決定 1, [[IADR-0424]] (#1364): REST 3 口が `ServiceCaller` を要するように
            // なったため、器も資格情報を扱えなければならない。**偽の認証スキームは置かない** ——
            // 本物の JwtBearer と `KeycloakRolesClaimsTransformation` を通す（`TestServiceTokens` の 🔴）。
            TestServiceTokens.UseStaticJwtBearer(services);
        });
    }

    /// <summary>既定のクライアントが名乗るサービス主体。</summary>
    public const string ServiceSubject = "service-account-retrieval-service";

    // 🔴 **既定のクライアントは `ServiceCaller` として認証済みである。** 既存の端点試験は
    // いずれも「サービスが呼ぶ」ことを書いており、資格情報の有無は主題ではない ——
    // ここで載せることで**既存の呼び出し 30 箇所超を 1 行も書き換えずに済む**
    // （`RetrievalService.Tests` が #1318 で採った作法と同型）。
    // 資格情報が無い側・利用者トークンの側は下の 2 つの口で作る。
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ServiceCallerToken());
    }

    /// <summary>
    /// NFR-09, [[IADR-0424]] (#1364): **資格情報を持たない**クライアント（陰性対照）。
    /// 3 口はいずれも 401 になる。
    /// </summary>
    public HttpClient CreateAnonymousClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    /// <summary>
    /// NFR-09, [[IADR-0424]] (#1364): **利用者のトークン**を持つクライアント（陰性対照）。
    /// 既定は管理者ロールである —— **管理者であっても通らない**ことがこの門の要点だからである。
    /// </summary>
    public HttpClient CreateUserClient(string role = PlatformAuthPolicies.AdminRole)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestServiceTokens.IssueToken("admin-user", [role]));
        return client;
    }

    /// <summary>`platform-service` ロールを持つ s2s トークン（陽性対照で使う）。</summary>
    public static string ServiceCallerToken() =>
        TestServiceTokens.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);
}

// テスト用スタブ LLM プロバイダー
// IADR-0101: 受け取った MaxTokens を本文へ反映し、既定値がプロバイダまで到達することを
// テストから検証できるようにする（既存アサーションは "テスト回答" の部分一致のため影響しない）。
file class StubLlmProvider : ILlmProvider
{
    public Task<CompletionResult> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => Task.FromResult(new CompletionResult($"テスト回答 maxTokens={req.MaxTokens}", 10, 20));
}

// テスト用スタブ埋め込みプロバイダー（要求次元どおりのゼロベクトルを返す）。
file class StubEmbeddingProvider : IEmbeddingProvider
{
    public Task<float[]> EmbedAsync(string text, string model, int dimensions, EmbeddingRoutePurpose purpose, CancellationToken ct = default)
        => Task.FromResult(new float[dimensions]);
}
