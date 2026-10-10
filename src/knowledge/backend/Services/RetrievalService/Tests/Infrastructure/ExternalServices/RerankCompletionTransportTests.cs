using AwesomeAssertions;
using Grpc.Core;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Platform.Shared.Infrastructure.Foundation.Observability;
using RetrievalService.Common.Observability;
using RetrievalService.Features.Search.Hybrid;
using RetrievalService.Infrastructure.ExternalServices;
using RetrievalService.Tests.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;
using PbSearch = Knowledge.Contracts.Grpc.Retrieval.V1;

namespace RetrievalService.Tests.Infrastructure.ExternalServices;

// FR-03, FR-11, NFR-02, ADR-0010, ADR-0076 決定 4, ADR-0127 決定 3, [[IADR-0498]] 決定 5・7（2026-10-06 追記 / #1746 監査 F1・F4・F5）:
// **再順位付けの輸送（gRPC）が、ゲートウェイへ何を送り、失敗・取り消しをどう上げるか。**
// ［2026-10-10 / #1255］[[IADR-0533]]: REST の輸送（`HttpRerankCompletionClient`）を撤去したので、その 2 本（本文・非 2xx）を外した。
//
//   - 送る中身: `purpose = rerank`・`confidentiality` = 段が算出した区分・本文・出力上限（proto の欄）
//   - 合成監視の標識: 合成のときだけ付く（gRPC のメタデータ）
//   - 🔴 gRPC の取り消し: 呼び出し元の ct による `RpcException(Cancelled / DeadlineExceeded)` は `OperationCanceledException`
//     へ写す（チャネルは `ThrowOperationCanceledOnCancellation` を立てていない）。取り消していない Cancelled は輸送の失敗のまま
//   - 検索サービスの受け口: gRPC のメタデータで来た標識を、段と同じ判定（`IHttpContextAccessor` 越し）が読める
[Trait("TestKind", "Unit")]
public class RerankCompletionTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CompletionApiRequest Body(string confidentiality = ConfidentialityLevels.Restricted) =>
        new("並べ替えの指示", 512, null, confidentiality, SearchRerankOptions.Purpose);

    // ───────────────────────── gRPC（LlmCompletion/Complete） ─────────────────────────

    // T-110 (gRPC): proto の欄に用途 rerank・算出した区分・出力上限が載り、合成のときだけ標識がメタデータに載る。
    [Theory]
    [InlineData(false, "confidential")]
    [InlineData(true, "restricted")]
    public async Task gRPC輸送は用途と区分をprotoで送り合成のときだけ標識を付ける(bool synthetic, string level)
    {
        var fake = new RecordingCompletionClient();

        await new GrpcRerankCompletionClient(fake).CompleteAsync(Body(level), synthetic, Ct);

        fake.LastRequest!.Purpose.Should().Be("rerank");
        fake.LastRequest.Confidentiality.Should().Be(level);
        fake.LastRequest.MaxTokens.Should().Be(512);
        var headers = (fake.LastOptions.Headers ?? []).ToList();
        if (synthetic)
            headers.Should().ContainSingle(h => h.Key == SyntheticTraffic.HeaderName.ToLowerInvariant()
                                               && h.Value == SyntheticTraffic.HeaderValue);
        else
            headers.Should().BeEmpty();
        fake.LastOptions.CancellationToken.Should().Be(Ct, "呼び出し元の ct を生成クライアントへ渡す");
    }

    // T-111 (gRPC・F4): 呼び出し元が取り消した `RpcException(Cancelled / DeadlineExceeded)` は `OperationCanceledException` で上がる。
    [Theory]
    [InlineData(StatusCode.Cancelled)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task gRPC輸送は呼び出し元の取り消しを取り消しとして上げる(StatusCode status)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var fake = new RecordingCompletionClient { CancelThenFail = (cts, status) };

        var act = () => new GrpcRerankCompletionClient(fake).CompleteAsync(Body(), false, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // T-111 (gRPC・F4 の対照): 取り消していないのに来た Cancelled は上流の不調であり、`RpcException` のまま（輸送の失敗）。
    [Fact]
    public async Task 取り消していないCancelledは輸送の失敗のまま()
    {
        var fake = new RecordingCompletionClient { Failure = new RpcException(new Status(StatusCode.Cancelled, "upstream")) };

        var act = () => new GrpcRerankCompletionClient(fake).CompleteAsync(Body(), false, Ct);

        await act.Should().ThrowAsync<RpcException>();
    }

    // T-111 (実チャネル): ループバックの受け口が応答しないまま、呼び出し元が取り消すと `OperationCanceledException`。
    // 段を通すと、利用者の取り消しは取り消しのまま上がり、段の期限は `timeout` として元の順へ戻る（`transport` と数え違えない）。
    [Fact]
    public async Task 実チャネルで取り消しと期限を区別する()
    {
        await using var server = await LoopbackGrpcServer.StartAsync(new HangingCompletion(), Ct);
        var transport = new GrpcRerankCompletionClient(new Pb.LlmCompletion.LlmCompletionClient(server.Channel));

        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(200));
            var act = () => transport.CompleteAsync(Body(), false, cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        using var meters = new TestMeterFactory();
        var degraded = new List<string>();
        using var listener = Listen(meters, degraded);
        var reranker = new ClaudeSearchReranker(transport,
            new SearchRerankOptions { Enabled = true, TimeoutSeconds = 1 }, new RerankMetrics(meters),
            NullLogger<ClaudeSearchReranker>.Instance);
        var a = Hit("a"); var b = Hit("b");

        var result = await reranker.RerankAsync(new SearchRequest("q", 10, null, null, SearchModes.Keyword),
            SearchSorts.Relevance, [a, b], Ct);
        result.Results.Should().Equal(a, b);
        result.Degraded.Should().BeTrue("#1871: 時間切れは応答の縮退の印（rerank-failed）へ写る");
        degraded.Should().Equal(RerankMetrics.Timeout);

        using var user = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        user.CancelAfter(TimeSpan.FromMilliseconds(200));
        var cancelled = () => reranker.RerankAsync(new SearchRequest("q", 10, null, null, SearchModes.Keyword),
            SearchSorts.Relevance, [a, b], user.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        degraded.Should().Equal([RerankMetrics.Timeout], "利用者の取り消しは縮退として数えない");
    }

    // ───────────────────────── 検索サービスの受け口（gRPC） ─────────────────────────

    // T-109 (受け口): gRPC のメタデータで来た標識を、段と同じ判定（`IHttpContextAccessor` 越しの受信ヘッダ）が読める。
    // 陰性対照: 標識の無い呼び出しは合成と見なされない。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task gRPCのメタデータの標識を受け口の判定が読める(bool synthetic)
    {
        var probe = new SyntheticProbeSearch(new HttpContextAccessor());
        await using var server = await LoopbackGrpcServer.StartAsync(probe, s => s.AddHttpContextAccessor(), Ct);
        var headers = new Metadata();
        SyntheticTraffic.PropagateTo(headers, synthetic);

        await new PbSearch.DocumentSearch.DocumentSearchClient(server.Channel)
            .SearchAsync(new PbSearch.SearchRequest { Query = "q" }, headers, cancellationToken: Ct);

        probe.SawSynthetic.Should().Be(synthetic);
    }

    private static SearchResultDto Hit(string name) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, name, 1f, null, new() { ["confidentiality"] = "public" }, []);

    private static System.Diagnostics.Metrics.MeterListener Listen(TestMeterFactory meters, List<string> degraded)
    {
        var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (i, l) =>
            {
                if (ReferenceEquals(i.Meter.Scope, meters) && i.Name == RerankMetrics.CounterName)
                    l.EnableMeasurementEvents(i);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string result = "", reason = "";
            foreach (var t in tags)
            {
                if (t.Key == RerankMetrics.ResultTag) result = t.Value?.ToString() ?? "";
                if (t.Key == RerankMetrics.ReasonTag) reason = t.Value?.ToString() ?? "";
            }
            if (result == RerankMetrics.Degraded) lock (degraded) degraded.Add(reason);
        });
        listener.Start();
        return listener;
    }

    private sealed class RecordingCompletionClient : Pb.LlmCompletion.LlmCompletionClient
    {
        public Pb.CompleteRequest? LastRequest { get; private set; }
        public CallOptions LastOptions { get; private set; }
        public Exception? Failure { get; init; }
        public (CancellationTokenSource Caller, StatusCode Status)? CancelThenFail { get; init; }

        public override AsyncUnaryCall<Pb.CompleteResponse> CompleteAsync(Pb.CompleteRequest request, CallOptions options)
        {
            LastRequest = request;
            LastOptions = options;
            Exception? failure = Failure;
            if (CancelThenFail is { } c)
            {
                c.Caller.Cancel();
                failure = new RpcException(new Status(c.Status, "Call canceled by the client."));
            }
            var task = failure is null
                ? Task.FromResult(LlmGrpcMapping.ToProto(new CompletionApiResponse("{\"ranking\":[1]}", "m", 1, 1)))
                : Task.FromException<Pb.CompleteResponse>(failure);
            return new(task, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
        }
    }

    // 応答しないゲートウェイ（呼び出し元の取り消しまで待つ）。
    private sealed class HangingCompletion : Pb.LlmCompletion.LlmCompletionBase
    {
        public override async Task<Pb.CompleteResponse> Complete(Pb.CompleteRequest request, ServerCallContext context)
        {
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            return new Pb.CompleteResponse();
        }
    }

    // 受け口で、段と同じ判定（`SyntheticTraffic.IsSyntheticInternalRequest` ＋ `IHttpContextAccessor`）を評価して記録する。
    private sealed class SyntheticProbeSearch(IHttpContextAccessor accessor) : PbSearch.DocumentSearch.DocumentSearchBase
    {
        public bool? SawSynthetic { get; private set; }

        public override Task<PbSearch.SearchResponse> Search(PbSearch.SearchRequest request, ServerCallContext context)
        {
            SawSynthetic = SyntheticTraffic.IsSyntheticInternalRequest(accessor.HttpContext?.Request);
            return Task.FromResult(new PbSearch.SearchResponse());
        }
    }
}
