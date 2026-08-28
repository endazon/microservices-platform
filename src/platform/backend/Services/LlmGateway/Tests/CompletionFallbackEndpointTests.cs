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
    // planning#426 裁定 (a) が鎖を足したのは default / rag-answer の 2 つだけであり、
    // report-weekly 等は依然として鎖を持たない。「鎖が無い用途は落ちない」という分岐そのものが
    // 生きていることを、鎖を得た rag-answer から別の用途へ移して固定し直す。
    [Fact]
    public async Task PostComplete_ReportWeekly_When400_DoesNotFallBack()
    {
        var client = ClientFailing(ModelScriptedProvider.AnyModel, HttpStatusCode.BadRequest);

        var req = new { Prompt = "週報", MaxTokens = 100, Confidentiality = "public", Purpose = "report-weekly" };
        var response = await client.PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeFalse();
        body.Model.Should().Be("claude-opus-5", "鎖が無いので第 1 候補のまま縮退する");
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
