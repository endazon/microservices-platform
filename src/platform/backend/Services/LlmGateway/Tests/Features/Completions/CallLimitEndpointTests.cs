using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace LlmGateway.Tests.Features.Completions;

// NFR-18, NFR-21, FR-11, ADR-0044, IADR-0678 (#2404): 送る前の呼び出しの上限を REST の面（POST /complete）で固定する。
//
// 🔴 **段 1（Report・既定）は送信を止めない。段 2（Enforce）は送らずに Sent=false・FailureKind=limit_exceeded で返す。**
// 呼び出しが送られたかどうかは、スタブの本文（"テスト回答"）が返ったかで見る。
// 値は文字列で書く（設定キー・FailureKind は配備と呼び手が読む外部の契約であるため、定数を引かずに綴りを固定する）。
//
// 器は試験ごとに WithWebHostBuilder で作り直す（上限の状態はプロセス内の 1 つの判定器が持つので、試験の間で混ざらない）。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public class CallLimitEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string LimitExceeded = "limit_exceeded";

    private WebApplicationFactory<Program> With(params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))));

    // NFR-18, IADR-0692: 段 2 は既定の当日の費用の上限が無いと起動しない。費用以外を見る試験では、
    // 判定に掛からない大きな金額を既定に置く（費用の試験は呼び手・用途の上限を足して見る）。
    private WebApplicationFactory<Program> Enforce(params (string Key, string Value)[] settings) =>
        With([("Llm:CallLimits:Mode", "Enforce"), ("Llm:CallLimits:Default:DailyCostLimit", "1000"), .. settings]);

    private static HttpClient As(WebApplicationFactory<Program> app, string subject)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestServiceTokens.IssueToken(subject, [PlatformAuthPolicies.ServiceRole]));
        return client;
    }

    private static async Task<CompletionApiResponse> PostAsync(
        HttpClient client, int maxTokens = 100, string purpose = "default", string prompt = "質問", bool synthetic = false)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/complete")
        {
            Content = JsonContent.Create(new CompletionApiRequest(prompt, MaxTokens: maxTokens, Purpose: purpose)),
        };
        if (synthetic)
            req.Headers.TryAddWithoutValidation(SyntheticTraffic.HeaderName, SyntheticTraffic.HeaderValue);
        var res = await client.SendAsync(req, TestContext.Current.CancellationToken);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<CompletionApiResponse>(TestContext.Current.CancellationToken))!;
    }

    private static void ShouldBeSent(CompletionApiResponse body)
    {
        body.Sent.Should().BeTrue();
        body.Text.Should().Contain("テスト回答");
        body.FailureKind.Should().BeNull();
    }

    private static void ShouldBeLimited(CompletionApiResponse body, string kind)
    {
        body.Sent.Should().BeFalse();
        body.FailureKind.Should().Be(LimitExceeded);
        body.Text.Should().NotContain("テスト回答", "上限で止めた呼び出しはプロバイダへ届かない");
        body.Text.Should().Contain(kind);
        body.InputTokens.Should().Be(0);
        body.OutputTokens.Should().Be(0);
    }

    // ---- 段 1（Report）: 送信は止めない --------------------------------------------------------------

    [Fact]
    public async Task 既定の段では上限を超えたmax_tokensでも送る()
    {
        using var app = With(("Llm:CallLimits:MaxOutputTokens", "1000"));

        ShouldBeSent(await PostAsync(As(app, "service-account-retrieval-service"), maxTokens: 2000));
    }

    [Fact]
    public async Task Reportでは要求数の上限を超えても送る()
    {
        using var app = With(
            ("Llm:CallLimits:Mode", "Report"),
            ("Llm:CallLimits:Default:PurposeRequestsPerMinute", "1"));
        var client = As(app, "service-account-retrieval-service");

        ShouldBeSent(await PostAsync(client));
        ShouldBeSent(await PostAsync(client));
    }

    // ---- 段 2（Enforce）: 入力の上限 ------------------------------------------------------------------

    [Fact]
    public async Task Enforceではmax_tokensの上限を超えた呼び出しを送らない()
    {
        using var app = Enforce(("Llm:CallLimits:MaxOutputTokens", "1000"));
        var client = As(app, "service-account-retrieval-service");

        ShouldBeLimited(await PostAsync(client, maxTokens: 1001), "max_tokens");
        ShouldBeSent(await PostAsync(client, maxTokens: 1000));
    }

    [Fact]
    public async Task Enforceでは0以下のmax_tokensを送らない()
    {
        using var app = Enforce();

        ShouldBeLimited(await PostAsync(As(app, "service-account-retrieval-service"), maxTokens: -1), "max_tokens");
    }

    [Fact]
    public async Task Enforceではプロンプト長の上限を超えた呼び出しを送らない()
    {
        using var app = Enforce(("Llm:CallLimits:MaxPromptChars", "10"));
        var client = As(app, "service-account-retrieval-service");

        ShouldBeLimited(await PostAsync(client, prompt: new string('あ', 11)), "prompt_length");
        ShouldBeSent(await PostAsync(client, prompt: new string('あ', 10)));
    }

    // ---- 段 2（Enforce）: 要求数の上限 ----------------------------------------------------------------

    [Fact]
    public async Task Enforceでは呼び手と用途の要求数の上限を超えた呼び出しを送らない_呼び手が違えば枠は別()
    {
        using var app = Enforce(
            ("Llm:CallLimits:Default:PurposeRequestsPerMinute", "2"));
        var retrieval = As(app, "service-account-retrieval-service");

        ShouldBeSent(await PostAsync(retrieval));
        ShouldBeSent(await PostAsync(retrieval));
        ShouldBeLimited(await PostAsync(retrieval), "purpose_rate");

        // 別の呼び手は自分の枠を持つ。
        ShouldBeSent(await PostAsync(As(app, "service-account-graph-service")));
    }

    [Fact]
    public async Task 用途の名前を未知の文字列へ変えても同じ1つの枠に数える()
    {
        using var app = Enforce(
            ("Llm:CallLimits:Default:PurposeRequestsPerMinute", "2"));
        var client = As(app, "service-account-retrieval-service");

        ShouldBeSent(await PostAsync(client, purpose: "unknown-purpose-1"));
        ShouldBeSent(await PostAsync(client, purpose: "unknown-purpose-2"));
        ShouldBeLimited(await PostAsync(client, purpose: "unknown-purpose-3"), "purpose_rate");
    }

    [Fact]
    public async Task 呼び手の全用途の合計の上限は用途を分けても効く()
    {
        using var app = Enforce(
            ("Llm:CallLimits:Default:RequestsPerMinute", "2"));
        var client = As(app, "service-account-retrieval-service");

        ShouldBeSent(await PostAsync(client, purpose: "default"));
        ShouldBeSent(await PostAsync(client, purpose: "rag-answer"));
        ShouldBeLimited(await PostAsync(client, purpose: "analysis"), "caller_rate");
    }

    [Fact]
    public async Task 合成監視の標識が付いた呼び出しも上限に数える()
    {
        using var app = Enforce(
            ("Llm:CallLimits:Default:PurposeRequestsPerMinute", "1"));
        var client = As(app, "service-account-retrieval-service");

        ShouldBeSent(await PostAsync(client, synthetic: true));
        ShouldBeLimited(await PostAsync(client, synthetic: true), "purpose_rate");
    }

    [Fact]
    public async Task 呼び手ごとの上書きは書かなかった項目に既定を使う()
    {
        using var app = Enforce(
            ("Llm:CallLimits:Default:PurposeRequestsPerMinute", "1"),
            // 上書きは全用途の合計だけを書く。用途ごとの上限（1）は既定から引き継ぐ。
            ("Llm:CallLimits:Callers:retrieval-service:RequestsPerMinute", "50"));
        var client = As(app, "service-account-retrieval-service");

        ShouldBeSent(await PostAsync(client));
        ShouldBeLimited(await PostAsync(client), "purpose_rate");
    }

    // ---- 段 2（Enforce）: 当日の費用の上限 -------------------------------------------------------------

    [Fact]
    public async Task Enforceでは当日の費用の上限を見込み額を含めて判定する()
    {
        // 既定の単価表（claude-opus-5-5: 出力 20 USD/MTok）で、出力の見込み 1,000,000 トークンは 20 USD。
        // スタブは入力 10・出力 20 トークンを返すので、送った後の実額はごく小さい。
        using var app = Enforce(
            ("Llm:CallLimits:MaxOutputTokens", "2000000"),
            ("Llm:CallLimits:Callers:retrieval-service:DailyCostLimit", "10"));
        var client = As(app, "service-account-retrieval-service");

        // 見込み額（20 USD）だけで上限（10 USD）を超える → 送らない。
        ShouldBeLimited(await PostAsync(client, maxTokens: 1_000_000), "caller_cost");
        // 見込みが小さい呼び出しは送れる（止めた呼び出しは枠を消費していない）。
        ShouldBeSent(await PostAsync(client, maxTokens: 100));
        ShouldBeSent(await PostAsync(client, maxTokens: 100));
    }

    [Fact]
    public async Task Enforceでは呼び手と用途の当日の費用の上限を見込み額を含めて判定し_他の用途は別の枠になる()
    {
        // caller_cost の試験と対にする。用途 default の上限だけを置く（見込み 20 USD > 10 USD）。
        using var app = Enforce(
            ("Llm:CallLimits:MaxOutputTokens", "2000000"),
            ("Llm:CallLimits:Callers:retrieval-service:PurposeDailyCostLimits:default", "10"));
        var client = As(app, "service-account-retrieval-service");

        ShouldBeLimited(await PostAsync(client, maxTokens: 1_000_000, purpose: "default"), "purpose_cost");
        // 同じ見込みでも、上限を置いていない用途は止めない（用途ごとの枠である）。
        ShouldBeSent(await PostAsync(client, maxTokens: 1_000_000, purpose: "rag-answer"));
        ShouldBeSent(await PostAsync(client, maxTokens: 100, purpose: "default"));
    }

    // ---- NFR-18, IADR-0692: 段 2 は当日の費用の上限が無いまま起動しない（fail-closed） ----------------

    [Fact]
    public void Enforceで当日の費用の上限が未設定なら起動で止まる()
    {
        using var app = With(("Llm:CallLimits:Mode", "Enforce"));

        var start = () => app.CreateClient();

        start.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>()
            .Which.Message.Should().Contain("Llm:CallLimits:Default:DailyCostLimit");
    }

    [Fact]
    public void Enforceで当日の費用の上限が0なら起動で止まる()
    {
        using var app = With(("Llm:CallLimits:Mode", "Enforce"), ("Llm:CallLimits:Default:DailyCostLimit", "0"));

        var start = () => app.CreateClient();

        start.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>();
    }

    [Fact]
    public async Task Enforceで既定の当日の費用の上限は呼び手の項目が無い呼び手にも効く()
    {
        // 既定の上限（10 USD）だけを置く。見込み額（20 USD）が既定を超える → 送らない。
        using var app = With(
            ("Llm:CallLimits:Mode", "Enforce"),
            ("Llm:CallLimits:MaxOutputTokens", "2000000"),
            ("Llm:CallLimits:Default:DailyCostLimit", "10"));
        var client = As(app, "service-account-graph-service");

        ShouldBeLimited(await PostAsync(client, maxTokens: 1_000_000), "caller_cost");
        ShouldBeSent(await PostAsync(client, maxTokens: 100));
    }
}
