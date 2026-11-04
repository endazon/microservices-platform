using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Grpc.Net.Client;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Ports;
using LlmGateway.Tests.Grpc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace LlmGateway.Tests.Features.Completions;

// FR-10, FR-11, ADR-0022, ADR-0044 決定 3, IADR-0671 (#2396): 呼び手が分けて渡した接頭辞がプロバイダへ届き、
// 応答のキャッシュの書き・読みが呼び手へ返り、費用の計器がそれを分けて積む（REST・gRPC の一括と逐次）。
//
// 🔴 応答の InputTokens は**プロンプト全体**（キャッシュを含む）であり、従前と同じ意味である。既存の呼び手（AST の費用計上は
// InputTokens × 入力単価・InputTokens で段を選ぶ）は、全体の意味が保たれる限り段の判定を誤らない。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public class CompletionPromptCacheEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string Prefix = "固定の前文\n";
    private const string Rest = "銘柄固有部";

    // T-2396-20: REST の一括。接頭辞がプロバイダへ届き、応答に全体と書き・読みが載り、計器が型を分けて積む。
    [Fact]
    public async Task PostComplete_WithCacheablePrefix_ForwardsPrefix_AndReturnsCacheUsage()
    {
        var provider = new RecordingProvider();
        var app = App(provider);
        var client = app.CreateClient();
        using var probe = new TokenProbe(app.Services);

        var response = await client.PostAsJsonAsync(
            "/complete",
            new CompletionApiRequest(Rest, MaxTokens: 100, Confidentiality: "public", Purpose: "default", CacheablePrefix: Prefix),
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CompletionApiResponse>(TestContext.Current.CancellationToken);
        body!.InputTokens.Should().Be(RecordingProvider.WholePrompt);
        body.CacheCreationInputTokens.Should().Be(RecordingProvider.Write);
        body.CacheReadInputTokens.Should().Be(RecordingProvider.Read);

        var received = provider.Requests.Should().ContainSingle().Subject;
        received.CacheablePrefix.Should().Be(Prefix);
        received.Prompt.Should().Be(Rest);

        probe.ByType().Should().BeEquivalentTo(RecordingProvider.ExpectedTokensByType);
    }

    // T-2396-21: 接頭辞の無い REST の要求は、接頭辞なしでプロバイダへ届き、応答の書き・読みは 0（既存の呼び手の挙動は変わらない）。
    [Fact]
    public async Task PostComplete_WithoutCacheablePrefix_IsUnchanged()
    {
        var provider = new RecordingProvider();
        var client = App(provider).CreateClient();

        var response = await client.PostAsJsonAsync(
            "/complete",
            new { Prompt = Prefix + Rest, MaxTokens = 100, Confidentiality = "public", Purpose = "default" },
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CompletionApiResponse>(TestContext.Current.CancellationToken);
        body!.InputTokens.Should().Be(RecordingProvider.LegacyInput);
        body.CacheCreationInputTokens.Should().Be(0);
        body.CacheReadInputTokens.Should().Be(0);
        provider.Requests.Should().ContainSingle().Which.CacheablePrefix.Should().BeNull();
    }

    // T-2396-22: gRPC の一括。proto の cacheable_prefix が届き、応答の cache_* が写って返る（写像の往復）。
    [Fact]
    public async Task GrpcComplete_WithCacheablePrefix_RoundTripsPrefixAndCacheUsage()
    {
        var provider = new RecordingProvider();
        var client = App(provider).CreateClient();
        using var channel = GrpcChannel.ForAddress(client.BaseAddress!, new GrpcChannelOptions { HttpClient = client });

        var reply = await new Pb.LlmCompletion.LlmCompletionClient(channel).CompleteAsync(
            LlmGrpcMapping.ToProto(new CompletionApiRequest(
                Rest, MaxTokens: 100, Confidentiality: "public", Purpose: "default", CacheablePrefix: Prefix)),
            cancellationToken: TestContext.Current.CancellationToken);

        reply.InputTokens.Should().Be(RecordingProvider.WholePrompt);
        reply.CacheCreationInputTokens.Should().Be(RecordingProvider.Write);
        reply.CacheReadInputTokens.Should().Be(RecordingProvider.Read);
        provider.Requests.Should().ContainSingle().Which.CacheablePrefix.Should().Be(Prefix);
    }

    // T-2396-23: gRPC の逐次。最終イベントに全体と書き・読みが載り、計器が型を分けて積む。
    [Fact]
    public async Task GrpcCompleteStream_WithCacheablePrefix_ReturnsCacheUsage_AndRecordsIt()
    {
        var provider = new RecordingProvider();
        var app = App(provider);
        var client = app.CreateClient();
        using var probe = new TokenProbe(app.Services);

        var events = await client.CompleteStreamAsync(
            new CompletionApiRequest(Rest, MaxTokens: 100, Confidentiality: "public", Purpose: "rag-answer", CacheablePrefix: Prefix),
            TestContext.Current.CancellationToken);

        var done = events.Should().ContainSingle(e => e.Done).Subject;
        done.Sent.Should().BeTrue();
        done.InputTokens.Should().Be(RecordingProvider.WholePrompt);
        done.CacheCreationInputTokens.Should().Be(RecordingProvider.Write);
        done.CacheReadInputTokens.Should().Be(RecordingProvider.Read);
        provider.Requests.Should().ContainSingle().Which.CacheablePrefix.Should().Be(Prefix);

        probe.ByType().Should().BeEquivalentTo(RecordingProvider.ExpectedTokensByType);
    }

    private WebApplicationFactory<Program> App(RecordingProvider provider) =>
        factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ILlmProvider>();
                s.AddKeyedSingleton<ILlmProvider>("claude", provider);
                s.AddKeyedSingleton<ILlmProvider>("selfhosted", provider);
                s.AddKeyedSingleton<ILlmProvider>("copilot", provider);
            }));

    // 受けた要求を記録し、接頭辞があればキャッシュの書き・読みを含む応答を返す。
    private sealed class RecordingProvider : ILlmProvider
    {
        public const int Write = 2_000;
        public const int Read = 10_000;
        public const int Uncached = 1_000;
        public const int WholePrompt = Uncached + Write + Read;
        public const int LegacyInput = 77;
        public const int Output = 500;

        public static readonly Dictionary<string, double> ExpectedTokensByType = new()
        {
            [LlmUsageMetrics.TokenTypeInput] = Uncached,
            [LlmUsageMetrics.TokenTypeCacheWrite] = Write,
            [LlmUsageMetrics.TokenTypeCacheRead] = Read,
            [LlmUsageMetrics.TokenTypeOutput] = Output,
        };

        private readonly List<CompletionRequest> _requests = [];

        public IReadOnlyList<CompletionRequest> Requests { get { lock (_requests) return [.. _requests]; } }

        public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
        {
            lock (_requests) _requests.Add(request);
            return Task.FromResult(request.HasCacheablePrefix
                ? new CompletionResult("ok", WholePrompt, Output, CompletionStopReasons.EndTurn, Write, Read)
                : new CompletionResult("ok", LegacyInput, Output, CompletionStopReasons.EndTurn));
        }

        public async IAsyncEnumerable<CompletionChunk> StreamAsync(
            CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (_requests) _requests.Add(request);
            await Task.Yield();
            yield return new CompletionChunk(string.Empty, Done: false, WholePrompt, 0, null, Write, Read);
            yield return new CompletionChunk("ok");
            yield return new CompletionChunk(string.Empty, Done: true, WholePrompt, Output, CompletionStopReasons.EndTurn, Write, Read);
        }
    }

    // 自分のアプリの容器の llm.tokens.total だけを型ごとに集める（IADR-0394: Meter のインスタンスで絞る）。
    private sealed class TokenProbe : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly Dictionary<string, double> _byType = [];

        public TokenProbe(IServiceProvider services)
        {
            _ = services.GetRequiredService<LlmUsageMetrics>();
            var meter = services.GetRequiredService<IMeterFactory>().Create(LlmUsageMetrics.MeterName);
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == LlmUsageMetrics.TokensCounterName)
                        l.EnableMeasurementEvents(instrument);
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, v, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key != LlmUsageMetrics.TokenTypeTag)
                        continue;
                    var type = tag.Value?.ToString() ?? string.Empty;
                    lock (_byType) _byType[type] = _byType.GetValueOrDefault(type) + v;
                }
            });
            _listener.Start();
        }

        public Dictionary<string, double> ByType() { lock (_byType) return new(_byType); }

        public void Dispose() => _listener.Dispose();
    }
}
