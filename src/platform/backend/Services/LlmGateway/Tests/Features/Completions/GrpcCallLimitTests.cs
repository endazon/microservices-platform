using System.Diagnostics.Metrics;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Tests.Grpc;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace LlmGateway.Tests.Features.Completions;

// NFR-18, IADR-0678 (#2404): gRPC の面（Complete・CompleteStream）も、検証済みトークンの主体を呼び手として上限の判定器へ渡す。
// gRPC の器は共有なので段 2 を組めない。既定の段（Report）で、超えた 1 件が gRPC のトークンの呼び手の名前で数えられることを見る
// （`Caller(context)` を外すと呼び手は unknown になり、この試験が落ちる）。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcCallLimitTests
{
    private readonly GrpcKestrelFactory _factory;

    public GrpcCallLimitTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private sealed class FixedTokenProvider(string token) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct) => ValueTask.FromResult(token);
    }

    private sealed class Probe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Dictionary<string, string>> _seen = [];

        public Probe()
        {
            _listener.InstrumentPublished = (i, l) =>
            {
                if (i.Name == LlmCallLimitMetrics.ExceededCounterName) l.EnableMeasurementEvents(i);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var d = new Dictionary<string, string>();
                foreach (var t in tags) d[t.Key] = t.Value?.ToString() ?? string.Empty;
                lock (_seen) _seen.Add(d);
            });
            _listener.Start();
        }

        public IReadOnlyList<Dictionary<string, string>> Seen { get { lock (_seen) return [.. _seen]; } }

        public void Dispose() => _listener.Dispose();
    }

    private Pb.LlmCompletion.LlmCompletionClient Client(string subject, out IDisposable channel)
    {
        var c = GrpcClientExtensions.CreatePlatformChannel(
            _factory.GrpcAddress,
            new FixedTokenProvider(GrpcKestrelFactory.IssueToken(subject, [PlatformAuthPolicies.ServiceRole])));
        channel = c;
        return new Pb.LlmCompletion.LlmCompletionClient(c);
    }

    // 既定の上限（MaxOutputTokens 32768）を超える max_tokens。
    private static Pb.CompleteRequest OverLimit() => new()
    {
        Prompt = "本文",
        MaxTokens = 40_000,
        Confidentiality = "public",
        Purpose = "rag-answer",
    };

    [Fact]
    public async Task 一括のgRPCは呼び手をトークンの主体から決めて数える()
    {
        using var probe = new Probe();
        var client = Client("service-account-graph-service", out var channel);
        using var _ = channel;

        (await client.CompleteAsync(OverLimit(), cancellationToken: TestContext.Current.CancellationToken))
            .Sent.Should().BeTrue("段 1 は止めない");

        probe.Seen.Should().Contain(m => m[LlmCallLimitMetrics.CallerTag] == "graph-service"
                                         && m[LlmCallLimitMetrics.KindTag] == "max_tokens");
    }

    [Fact]
    public async Task 逐次のgRPCも呼び手をトークンの主体から決めて数える()
    {
        using var probe = new Probe();
        var client = Client("service-account-conversion-service", out var channel);
        using var _ = channel;

        using var call = client.CompleteStream(OverLimit(), cancellationToken: TestContext.Current.CancellationToken);
        while (await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken)) { }

        probe.Seen.Should().Contain(m => m[LlmCallLimitMetrics.CallerTag] == "conversion-service"
                                         && m[LlmCallLimitMetrics.KindTag] == "max_tokens");
    }
}
