using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using LlmGateway.Domain.Ports;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Tests;

// T-25, FR-11, ADR-0038 決定 3・4 (#863), IADR-0225:
// /complete が **実設定（appsettings.json）の PurposeFallbackModels 経由で** 400 系のときだけ
// 次の候補モデルへ落ち、429 では落ちないことを HTTP 経路で固定する。
//
// **実設定を通すことに意味がある** —— 合成 config だけで検証すると、`appsettings.json` に鎖を
// 書き忘れても緑になる（IADR-0102 / IADR-0106 が実際に踏んだ「無音失効」と同型）。
//
// IADR-0110 (#395): メトリクス購読テスト（CompletionMetricsTests）と直列化する。
[Collection(CompletionEndpointCollection.Name)]
public class CompletionFallbackEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private record CompletionResponse(
        string Text, string Model, int InputTokens, int OutputTokens,
        bool Sent, string? Endpoint, string? RoutingReason);

    // 指定モデルへの呼び出しだけを指定ステータスで失敗させ、他は成功させるプロバイダ。
    private HttpClient ClientFailing(string failingModel, HttpStatusCode status)
        => factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ILlmProvider>();
                var provider = new ModelScriptedProvider(failingModel, status);
                s.AddKeyedSingleton<ILlmProvider>("claude", provider);
                s.AddKeyedSingleton<ILlmProvider>("selfhosted", provider);
                s.AddKeyedSingleton<ILlmProvider>("copilot", provider);
            })).CreateClient();

    private static object AnalysisRequest() =>
        new { Prompt = "機密文書の分析", MaxTokens = 100, Confidentiality = "public", Purpose = "analysis" };

    // T-25a, ADR-0038 決定 3: analysis の第 1 候補（claude-opus-5）が HTTP 400 で失敗したら、
    // 第 2 候補 claude-sonnet-5 へ落ちて応答が返る。応答の Model は**実際に投げたモデル**である
    // （IADR-0111: 使用モデルを偽らない）。
    [Fact]
    public async Task PostComplete_Analysis_When400_FallsBackToSonnet5()
    {
        var client = ClientFailing("claude-opus-5", HttpStatusCode.BadRequest);

        var response = await client.PostAsJsonAsync("/complete", AnalysisRequest(), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeTrue("400 系はフォールバックの発火条件である（ADR-0038 決定 4）");
        body.Model.Should().Be("claude-sonnet-5");
        body.Model.Should().NotBe("claude-opus-5");
        body.Endpoint.Should().Be("claude-managed");
    }

    // ★ T-25b, ADR-0038 決定 4 の核心: **429 ではフォールバックしない。**
    // 429 はレート制限であり再試行の対象である。別モデルへ逃がすと、ピン Runbook が定めた
    // 「利用不能時に別モデルへ切り替えない」という禁止を実質的に破る経路になる。
    [Fact]
    public async Task PostComplete_Analysis_When429_DoesNotFallBack()
    {
        var client = ClientFailing("claude-opus-5", HttpStatusCode.TooManyRequests);

        var response = await client.PostAsJsonAsync("/complete", AnalysisRequest(), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeFalse("429 は再試行であってフォールバックではない（ADR-0038 決定 4）");
        body.Model.Should().Be("claude-opus-5", "見送らずに第 1 候補のまま縮退する");
        body.Text.Should().Contain("現在利用できません");
    }

    // T-25c: 5xx も従来どおり縮退する（決定 4 が挙げるのは 400 系だけである）。
    [Fact]
    public async Task PostComplete_Analysis_When5xx_DoesNotFallBack()
    {
        var client = ClientFailing("claude-opus-5", HttpStatusCode.InternalServerError);

        var response = await client.PostAsJsonAsync("/complete", AnalysisRequest(), TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeFalse();
        body.Model.Should().Be("claude-opus-5");
    }

    // T-25d: 鎖が尽きたら（全候補が 400 で失敗）従来の縮退へ合流する。最後に投げたモデルを名乗る。
    [Fact]
    public async Task PostComplete_Analysis_WhenAllCandidatesFail_DegradesWithLastModel()
    {
        var client = ClientFailing(ModelScriptedProvider.AnyModel, HttpStatusCode.BadRequest);

        var response = await client.PostAsJsonAsync("/complete", AnalysisRequest(), TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeFalse();
        body.Model.Should().Be("claude-sonnet-5", "鎖の最後の候補まで試したことが応答から読める");
    }

    // T-25e: rag-answer は第 1 候補 claude-sonnet-5 が 400 で失敗したら claude-haiku-4-5 へ落ちる。
    // ［2026-08-21 / planning#426 裁定 (a)］それまで「第 2 候補は計画側で未確定であり実装が勝手に
    // 補わない」としてフォールバックしないことを固定していたが、裁定により鎖の登録が認められた。
    // 旧テスト名は PostComplete_RagAnswer_When400_DoesNotFallBack。
    [Fact]
    public async Task PostComplete_RagAnswer_When400_FallsBackToHaiku45()
    {
        var client = ClientFailing("claude-sonnet-5", HttpStatusCode.BadRequest);

        var req = new { Prompt = "要約", MaxTokens = 100, Confidentiality = "public", Purpose = "rag-answer" };
        var response = await client.PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeTrue("400 系はフォールバックの発火条件である（ADR-0038 決定 4）");
        body.Model.Should().Be("claude-haiku-4-5");
        body.Endpoint.Should().Be("claude-managed");
    }

    // T-25e2: **鎖を持たない用途は 400 でもフォールバックしない**（従来の挙動が変わっていないこと）。
    // ［2026-09-02 / AST#571 で改訂］旧テストは report-weekly を用いていたが、二段判断の層別用途登録の
    // 実装 ADR で report-weekly にも第 2 候補（claude-sonnet-5）を登録したため、鎖を持たない例として
    // 使えなくなった。**取引判断の一次スクリーニング（trade-decision-screening）はフォールバック禁止
    // （本判断と同じ理由。AST/ADR-0017 決定2）であり、恒久的に鎖を持たない用途**として移し替える。
    // 「鎖が無い用途は落ちない」という分岐そのものが生きていることを固定し続ける。
    [Fact]
    public async Task PostComplete_TradeDecisionScreening_When400_DoesNotFallBack()
    {
        var client = ClientFailing(ModelScriptedProvider.AnyModel, HttpStatusCode.BadRequest);

        var req = new { Prompt = "銘柄の一次絞り込み", MaxTokens = 100, Confidentiality = "internal", Purpose = "trade-decision-screening" };
        var response = await client.PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeFalse();
        body.Model.Should().Be("claude-haiku-4-5", "鎖が無いので第 1 候補のまま縮退する");
    }

    // AST#571, AST/ADR-0017 決定1: 報告書 3 種は取引判断と異なりフォールバックを許す。第 1 候補が
    // HTTP 400 で失敗したら、二段判断の層別用途登録の実装 ADR が定めた第 2 候補へ落ちて応答が返る。
    // いずれも安価側への遷移であり（月報・週報は opus-5→sonnet-5、日報は sonnet-5→haiku-4-5）、
    // 費用が上振れすることはない。
    [Theory]
    [InlineData("report-monthly", "claude-opus-5", "claude-sonnet-5")]
    [InlineData("report-weekly", "claude-opus-5", "claude-sonnet-5")]
    [InlineData("report-daily", "claude-sonnet-5", "claude-haiku-4-5")]
    public async Task PostComplete_ReportKindPurpose_When400_FallsBackToKindSpecificModel(
        string purpose, string primaryModel, string expectedFallbackModel)
    {
        var client = ClientFailing(primaryModel, HttpStatusCode.BadRequest);

        var req = new { Prompt = "報告書の散文", MaxTokens = 100, Confidentiality = "internal", Purpose = purpose };
        var response = await client.PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeTrue("400 系はフォールバックの発火条件である（AST/ADR-0017 決定1）");
        body.Model.Should().Be(expectedFallbackModel);
        body.Model.Should().NotBe(primaryModel);
        body.Endpoint.Should().Be("claude-managed");
    }

    // T-25f: **ストリーム経路はフォールバックしない**（IADR-0225 の射程外）。
    // analysis は非ストリーミング /complete を使うため実運用に穴は無いが、経路によって挙動が
    // 違うことをテストとして明示しておく（後から「落ちるはず」と読み違えないため）。
    [Fact]
    public async Task PostCompleteStream_Analysis_When400_DoesNotFallBack()
    {
        var client = ClientFailing("claude-opus-5", HttpStatusCode.BadRequest);

        var response = await client.PostAsJsonAsync("/complete/stream", AnalysisRequest(), TestContext.Current.CancellationToken);

        var sse = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        sse.Should().Contain("\"sent\":false");
        sse.Should().NotContain("claude-sonnet-5");
    }

    // 指定モデル（または全モデル）への呼び出しを HTTP ステータス付きの例外で失敗させるスタブ。
    // Anthropic.SDK / EnsureSuccessStatusCode と同じ形（HttpRequestException.StatusCode）で投げる。
    private sealed class ModelScriptedProvider(string failingModel, HttpStatusCode status) : ILlmProvider
    {
        public const string AnyModel = "*";

        public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
        {
            if (Fails(request.Model))
                throw new HttpRequestException($"upstream rejected {request.Model}", null, status);

            return Task.FromResult(new CompletionResult($"テスト回答 model={request.Model}", 10, 20));
        }

        public async IAsyncEnumerable<CompletionChunk> StreamAsync(
            CompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (Fails(request.Model))
                throw new HttpRequestException($"upstream rejected {request.Model}", null, status);

            await Task.CompletedTask;
            yield return new CompletionChunk($"テスト回答 model={request.Model}", Done: true, 10, 20);
        }

        private bool Fails(string? model)
            => failingModel == AnyModel || string.Equals(model, failingModel, StringComparison.Ordinal);
    }
}
