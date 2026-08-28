using System.Runtime.CompilerServices;
using AiAnalysisService.Domain.Ports;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiAnalysisService.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                ["Services:AuthorizationService"] = "http://localhost:5005",
                ["Services:RetrievalService"] = "http://localhost:5003",
                ["Services:LlmGateway"] = "http://localhost:5007"
            }));
        builder.ConfigureServices(services =>
        {
            // RAG オーケストレーターをスタブへ差し替え
            services.RemoveAll<IRagOrchestrator>();
            services.AddSingleton<IRagOrchestrator, StubRagOrchestrator>();
        });
    }
}

// テスト用スタブ RAG オーケストレーター（番号付き出典を含む回答を返す）
//
// **［#539］受け取った対象範囲を記録する。** 端点が対象範囲を後段へ渡しているかは、
// **端点の外からは観測できない**（回答本文は範囲に依存しない）ためである。
// `file` から `internal` へ上げたのは、別のテストファイルから記録を読むためである。
//
// **［#641 CI の実測］記録は `static` にしてはならない。**
// xUnit は**テストクラスを並列に走らせる**ので、`/analysis/ask/stream` を叩く別のクラス
// （`AskStreamEndpointTests` 等）の要求が、こちらの記録を上書きする。
// **手元では通り、CI で落ちた**（`LastStreamFilters` が null になった）。
// **インスタンスの状態にすれば安全である**——スタブは factory ごとの singleton であり、
// `IClassFixture` はクラスごとに factory を作るため、クラス間で共有されない。
internal sealed class StubRagOrchestrator : IRagOrchestrator
{
    // 直近に受け取った対象範囲（`ask` / `ask/stream` それぞれ）。
    public Dictionary<string, List<string>>? LastAskFilters { get; private set; }
    public Dictionary<string, List<string>>? LastStreamFilters { get; private set; }

    public void ResetRecording()
    {
        LastAskFilters = null;
        LastStreamFilters = null;
    }

    public Task<AiAnswerDto> AskAsync(string question, string userId,
        Dictionary<string, string> userAttributes,
        Dictionary<string, List<string>>? attributeFilters = null,
        CancellationToken ct = default)
    {
        LastAskFilters = attributeFilters;
        return Task.FromResult(Answer("テスト回答 [1]"));
    }

    public Task<AiAnswerDto> AnalyzeAsync(AnalysisTaskRequest request, string userId,
        Dictionary<string, string> userAttributes, CancellationToken ct = default)
        => Task.FromResult(Answer($"分析結果({request.TaskType}) [1]"));

    // IADR-0037: ストリーミングのスタブ（citations → token* → done）。
    public async IAsyncEnumerable<AskEvent> AskStreamAsync(string question, string userId,
        Dictionary<string, string> userAttributes,
        Dictionary<string, List<string>>? attributeFilters = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastStreamFilters = attributeFilters;
        yield return new AskCitationsEvent(
            [new CitationDto(1, Guid.NewGuid(), "文書A", Guid.NewGuid(), "s3://bucket/a.md", 0.9f, "抜粋")]);
        yield return new AskTokenEvent("テスト");
        yield return new AskTokenEvent("回答 [1]");
        await Task.Yield();
        yield return new AskDoneEvent(Guid.NewGuid(), "claude-sonnet-4-6", 10, 20);
    }

    private static AiAnswerDto Answer(string text)
        => new(
            text,
            [new CitationDto(1, Guid.NewGuid(), "文書A", Guid.NewGuid(),
                "s3://bucket/a.md", 0.9f, "抜粋")],
            "claude-sonnet-4-6", 10, 20);
}
