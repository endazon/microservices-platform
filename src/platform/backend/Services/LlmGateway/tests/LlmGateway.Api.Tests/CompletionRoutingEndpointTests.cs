using FluentAssertions;
using LlmGateway.Api.Foundation.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace LlmGateway.Api.Tests;

// FR-11, ADR-0010: /complete が機密区分・用途に応じて呼び出し先を切り替え、
// 許容ティアが無い場合は送信を拒否（縮退）することを検証する。
public class CompletionRoutingEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private record CompletionResponse(
        string Text, string Model, int InputTokens, int OutputTokens,
        bool Sent, string? Endpoint, string? RoutingReason);

    // IADR-0101, FR-11: maxTokens を省略した JSON では、共有契約 CompletionApiRequest の既定値
    // （4096）がプロバイダまで渡ることを固定する。HTTP 経路で実際に効く既定は ILlmProvider 側の
    // CompletionRequest ではなく DTO 側であり（エンドポイントは req.MaxTokens を常に明示的に渡す）、
    // ここが 1024 に戻ると thinking 既定有効モデルで本文が空になる回帰が起きる。
    // レコードのコンストラクタ既定値が System.Text.Json のバインドで尊重されることの担保も兼ねる。
    [Fact]
    public async Task PostComplete_WithoutMaxTokens_PassesContractDefaultToProvider()
    {
        var req = new { Prompt = "要約", Confidentiality = "public", Purpose = "default" };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>();
        body!.Sent.Should().BeTrue();
        body.Text.Should().Contain("maxTokens=4096");
    }

    // FR-11: 既定構成（ティアB=claude 有効）では confidential でも保護契約済み外部APIへ送信できる。
    [Fact]
    public async Task PostComplete_Confidential_RoutesToProtectedExternalAndSends()
    {
        var req = new { Prompt = "機密文書の要約", MaxTokens = 100, Confidentiality = "confidential", Purpose = "analysis" };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>();
        body!.Sent.Should().BeTrue();
        body.Endpoint.Should().Be("claude-managed");
        body.Text.Should().NotBeNullOrWhiteSpace();
    }

    // FR-11: Model 未指定なら用途（purpose）に応じてモデルを切り替える（analysis→fable-5 / rag-answer→sonnet / diagram-coding→haiku）。
    // ADR-0010 / IADR-0022: 既定 opus / 定型 sonnet・haiku / 最難関 analysis→fable-5。
    // 実運用経路（RagOrchestrator / LlmGatewayDiagramCoder）は Model=null で /complete を呼ぶため、この経路で用途別モデルが発火することを検証する。
    // purpose 値は呼び出し側が送る文字列（ConversionService は "diagram-coding"）と一致させる（設定キー統一のガード）。
    // ZDR 非対応の fable-5 は confidential/restricted では除外されるため、用途別モデルの発火は public で検証する。
    [Theory]
    [InlineData("analysis", "claude-fable-5")]
    [InlineData("rag-answer", "claude-sonnet-4-6")]
    [InlineData("diagram-coding", "claude-haiku-4-5")]
    public async Task PostComplete_WithoutExplicitModel_SelectsPurposeModel(string purpose, string expectedModel)
    {
        var req = new { Prompt = "要約", MaxTokens = 100, Confidentiality = "public", Purpose = purpose };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>();
        body!.Sent.Should().BeTrue();
        // 用途別モデルが選択される（呼び出し元が既定モデルを固定送信しないため）。
        body.Model.Should().Be(expectedModel);
    }

    // IADR-0022 / 08_data-egress-policy: confidential の analysis は ZDR 非対応の fable-5 を除外し、
    // ZDR 対応の既定モデル（opus）へフォールバックしたうえで送信する。
    [Fact]
    public async Task PostComplete_ConfidentialAnalysis_FallsBackToZdrModel()
    {
        var req = new { Prompt = "機密文書の分析", MaxTokens = 100, Confidentiality = "confidential", Purpose = "analysis" };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>();
        body!.Sent.Should().BeTrue();
        body.Endpoint.Should().Be("claude-managed");
        body.Model.Should().Be("claude-opus-5");
        body.Model.Should().NotBe("claude-fable-5");
    }

    // FR-11 / deny-by-default: confidentiality 未指定は安全側（restricted 相当）に倒し、ティアC のみの構成では送信を拒否する。
    [Fact]
    public async Task PostComplete_WithoutConfidentiality_FallsBackToRestrictedAndRefusesTierCOnly()
    {
        var client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
                s.Configure<LlmRoutingOptions>(o =>
                {
                    o.AllowUnapprovedTierC = false;
                    o.Endpoints =
                    [
                        new LlmEndpointOptions
                        {
                            Name = "standard-external",
                            Tier = ProtectionTier.C,
                            Provider = "claude",
                            Enabled = true,
                            Priority = 1,
                            DefaultModel = "std",
                            Models = ["std"]
                        }
                    ];
                }))).CreateClient();

        // Confidentiality を指定しない（null）。安全側では restricted 相当 → ティアC 不可で拒否。
        var req = new { Prompt = "分類不明の文書", MaxTokens = 100, Purpose = "analysis" };
        var response = await client.PostAsJsonAsync("/complete", req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>();
        body!.Sent.Should().BeFalse();
        body.RoutingReason.Should().Contain("拒否");
    }

    // FR-11: 送信先ティアが許容されない構成（confidential に対しティアCのみ）では送信を拒否する。
    [Fact]
    public async Task PostComplete_Confidential_WhenOnlyStandardExternal_IsRefused()
    {
        var client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
                s.Configure<LlmRoutingOptions>(o =>
                {
                    // 越境の許容されないティアC（標準外部API）のみへ差し替える。
                    o.AllowUnapprovedTierC = false;
                    o.Endpoints =
                    [
                        new LlmEndpointOptions
                        {
                            Name = "standard-external", Tier = ProtectionTier.C, Provider = "claude",
                            Enabled = true, Priority = 1, DefaultModel = "std", Models = ["std"]
                        }
                    ];
                }))).CreateClient();

        var req = new { Prompt = "機密文書の要約", MaxTokens = 100, Confidentiality = "confidential", Purpose = "analysis" };
        var response = await client.PostAsJsonAsync("/complete", req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>();
        body!.Sent.Should().BeFalse();          // 外部送信していない（縮退）
        body.RoutingReason.Should().Contain("拒否");
    }
}
