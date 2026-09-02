using AiAnalysisService.Domain.Ports;
using AiAnalysisService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;

namespace AiAnalysisService.Tests.Infrastructure.ExternalServices;

// FR-05: ABAC スコープ解決の通信失敗時に deny-by-default へ縮退することを検証する。
public class RagOrchestratorScopeTests
{
    // 認可サービスへの通信が例外（ネットワーク障害・タイムアウト）で失敗しても、
    // 500 を伝播させず空回答（deny-by-default）へ縮退する。
    [Fact]
    public async Task AskAsync_AuthzHttpFailure_DegradesToEmptyAnswer()
    {
        var orchestrator = new RagOrchestrator(
            new ThrowingHttpClientFactory(new HttpRequestException("connection refused")));

        AiAnswerDto? answer = null;
        var act = async () => answer = await orchestrator.AskAsync(
            "質問", "user-1", new Dictionary<string, string>());

        await act.Should().NotThrowAsync();
        answer!.Citations.Should().BeEmpty();
    }

    // タイムアウト（HttpClient は TaskCanceledException を投げる）でも同様に縮退する。
    [Fact]
    public async Task AskAsync_AuthzTimeout_DegradesToEmptyAnswer()
    {
        var orchestrator = new RagOrchestrator(
            new ThrowingHttpClientFactory(new TaskCanceledException("timeout")));

        AiAnswerDto? answer = null;
        var act = async () => answer = await orchestrator.AskAsync(
            "質問", "user-1", new Dictionary<string, string>());

        await act.Should().NotThrowAsync();
        answer!.Citations.Should().BeEmpty();
    }

    // IADR-0037, IADR-0009, FR-05: ストリーミングでも権限が無い場合（deny-by-default）は非ストリーミング版と
    // 挙動を揃え、citations（空）→ 中立文言 token → done を送る（本文が空のまま done になり理由不明の
    // 空白回答が表示されるのを防ぐ）。存在秘匿を破らない中立文言であること。
    [Fact]
    public async Task AskStreamAsync_Denied_EmitsNeutralMessageBeforeDone()
    {
        var orchestrator = new RagOrchestrator(
            new ThrowingHttpClientFactory(new HttpRequestException("connection refused")));

        var events = new List<AskEvent>();
        await foreach (var ev in orchestrator.AskStreamAsync(
            "質問", "user-1", new Dictionary<string, string>(), ct: TestContext.Current.CancellationToken))
        {
            events.Add(ev);
        }

        // 出典は空、本文（token）に中立文言、最後に done。
        events.Should().ContainSingle(e => e is AskCitationsEvent)
            .Which.As<AskCitationsEvent>().Citations.Should().BeEmpty();
        events.OfType<AskTokenEvent>().Should().ContainSingle()
            .Which.Text.Should().Be("閲覧権限のある文書が見つかりませんでした。");
        events.Last().Should().BeOfType<AskDoneEvent>();
    }

    // 生成する HttpClient がすべて指定の例外を投げるスタブファクトリ。
    private sealed class ThrowingHttpClientFactory(Exception toThrow) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new ThrowingHandler(toThrow)) { BaseAddress = new Uri("http://localhost") };
    }

    private sealed class ThrowingHandler(Exception toThrow) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw toThrow;
    }
}
