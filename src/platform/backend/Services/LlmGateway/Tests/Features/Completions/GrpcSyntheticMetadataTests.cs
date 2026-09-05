using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using LlmGateway.Common.Observability;
using LlmGateway.Tests.Grpc;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Observability;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace LlmGateway.Tests.Features.Completions;

// NFR-02, FR-10, ADR-0044, ADR-0076 決定 4, IADR-0378, IADR-0394, IADR-0398 決定 3 (#1255):
// **合成監視の標識はメタデータ `x-synthetic-traffic` で運び、gRPC 面でも費用から除外される。**
//
// 🔴 標識を**本文（proto）に置かなかった**ことの実効性を測る試験でもある ——
// メタデータで届いた標識が、REST と同じ判定関数
// （`SyntheticTraffic.IsSyntheticInternalRequest(GetHttpContext().Request)`）で読めなければ、
// gRPC 経路の合成トラフィックが**費用に混ざる**（例外にはならない）。
//
// 🔴 **陽性と陰性を対で置く。** 「合成は外れる」だけでは、計器がそもそも動いていなくても緑になる。
// IADR-0394 決定 1: MeterListener は Meter の**インスタンス**で絞る（名前で絞ると他クラスの発行を拾う）。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcSyntheticMetadataTests
{
    private readonly GrpcKestrelFactory _factory;

    public GrpcSyntheticMetadataTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private sealed record Measured(string Instrument, double Value);

    private sealed class UsageProbe : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Measured> _items = [];

        public UsageProbe(IServiceProvider services)
        {
            // 🔴 計器の生成を先に済ませる（解決するまでカウンタが存在せず、購読しているつもりで
            // 何も見ていない状態になる。IADR-0394 と同じ作法）。
            _ = services.GetRequiredService<LlmUsageMetrics>();
            var meter = services.GetRequiredService<IMeterFactory>().Create(LlmUsageMetrics.MeterName);

            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter)
                        && instrument.Name is LlmUsageMetrics.TokensCounterName
                            or LlmUsageMetrics.CostCounterName
                            or LlmUsageMetrics.SyntheticExcludedCounterName)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, _, _) => Add(i.Name, v));
            _listener.SetMeasurementEventCallback<double>((i, v, _, _) => Add(i.Name, v));
            _listener.Start();
        }

        private void Add(string name, double value)
        {
            lock (_items) _items.Add(new Measured(name, value));
        }

        public IReadOnlyList<Measured> Items { get { lock (_items) return [.. _items]; } }
        public void Dispose() => _listener.Dispose();
    }

    private Pb.LlmCompletion.LlmCompletionClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static Metadata Headers(bool synthetic)
    {
        var headers = new Metadata
        {
            { "Authorization", "Bearer " + GrpcKestrelFactory.IssueToken(
                "service-account-aianalysis-service", [PlatformAuthPolicies.ServiceRole]) },
        };
        // 🔴 送る側も**単一情報源の多重定義**を通す（gRPC 用のヘルパを別に持たない）。
        SyntheticTraffic.PropagateTo(headers, synthetic);
        return headers;
    }

    private static Pb.CompleteRequest Request => new()
    {
        Prompt = "監視用の代表リクエスト",
        MaxTokens = 100,
        Confidentiality = "public",
        Purpose = "default",
    };

    // ★ 陰性対照: 標識が無い補完は従来どおり費用（トークン累計）へ計上される。
    // **絞り込みが「何も拾わない」に退化していないことの対照でもある。**
    [Fact]
    public async Task Grpc_complete_without_marker_records_usage()
    {
        using var probe = new UsageProbe(_factory.Services);

        var resp = await PlainClient().CompleteAsync(
            Request, headers: Headers(synthetic: false),
            cancellationToken: TestContext.Current.CancellationToken);

        resp.Sent.Should().BeTrue();
        probe.Items.Should().Contain(m => m.Instrument == LlmUsageMetrics.TokensCounterName,
            "標識の無い呼び出しは費用計測へ載る");
        probe.Items.Should().NotContain(m => m.Instrument == LlmUsageMetrics.SyntheticExcludedCounterName);
    }

    // ★ 陽性: `x-synthetic-traffic` メタデータ付きの呼び出しは費用へ計上されず、
    // **除外した件数が別の計器に載る**（黙って落とさない。ADR-0076 決定 4）。
    [Fact]
    public async Task Grpc_complete_with_metadata_marker_is_excluded_from_cost()
    {
        using var probe = new UsageProbe(_factory.Services);

        var resp = await PlainClient().CompleteAsync(
            Request, headers: Headers(synthetic: true),
            cancellationToken: TestContext.Current.CancellationToken);

        resp.Sent.Should().BeTrue("合成でも送信そのものは成立する（除外されるのは費用計上だけ）");
        probe.Items.Should().Contain(m => m.Instrument == LlmUsageMetrics.SyntheticExcludedCounterName,
            "除外は黙って落とさず件数を積む");
        probe.Items.Should().NotContain(m => m.Instrument == LlmUsageMetrics.TokensCounterName,
            "合成監視のトラフィックは費用計測へ載らない");
        probe.Items.Should().NotContain(m => m.Instrument == LlmUsageMetrics.CostCounterName);
    }
}
