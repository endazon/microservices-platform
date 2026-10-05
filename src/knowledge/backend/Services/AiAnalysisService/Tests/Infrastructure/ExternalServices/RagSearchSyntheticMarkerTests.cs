using System.Net;
using System.Text;
using AiAnalysisService.Domain.Ports;
using AiAnalysisService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Grpc.Core;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Observability;
using Pb = Knowledge.Contracts.Grpc.Retrieval.V1;

namespace AiAnalysisService.Tests.Infrastructure.ExternalServices;

// FR-03, FR-04, NFR-02, ADR-0076 決定 4, ADR-0127 決定 3, [[IADR-0378]], [[IADR-0498]] 決定 2（2026-10-06 追記 / #1746 監査 F1）:
// **合成監視の標識を、RAG の検索から検索サービスへ引き継ぐ。**
//
// RAG は LLM の抑止（`SuppressLlmForSynthetic`）より**前に**検索する。検索サービスの再順位付けの段は内周の標識で
// 合成監視を見分けて LLM を呼ばないので、標識が届かないと合成監視のたびに再順位付けの費用が実利用として積まれる。
// 🔴 陽性（合成なら付く）と陰性対照（通常は付かない）を輸送ごとに対で置く。
[Trait("TestKind", "Unit")]
public class RagSearchSyntheticMarkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RagSearchQuery Query(bool synthetic) =>
        new("質問", 5, new AccessScope([], GrantsAccess: true), "alice",
            new Dictionary<string, string>(), null, synthetic);

    // T-109 (オーケストレーター): 受信要求の内周の標識を `RagSearchQuery.IsSynthetic` へ写す（質問・分析・逐次の 3 経路）。
    [Theory]
    [InlineData("ask", true)]
    [InlineData("ask", false)]
    [InlineData("analyze", true)]
    [InlineData("stream", true)]
    [InlineData("stream", false)]
    public async Task 受信要求の標識を検索の問い合わせへ写す(string path, bool synthetic)
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        if (synthetic)
            http.HttpContext!.Request.Headers[SyntheticTraffic.HeaderName] = SyntheticTraffic.HeaderValue;
        var transport = new RecordingTransport();
        var orchestrator = new RagOrchestrator(new ScopeOnlyFactory(), http, searchTransport: transport);

        switch (path)
        {
            case "ask":
                await orchestrator.AskAsync("質問", "u", new Dictionary<string, string>(), null, Ct);
                break;
            case "analyze":
                await orchestrator.AnalyzeAsync(new AnalysisTaskRequest("比較して"), "u",
                    new Dictionary<string, string>(), Ct);
                break;
            default:
                await foreach (var _ in orchestrator.AskStreamAsync("質問", "u", new Dictionary<string, string>(), null, Ct))
                { }
                break;
        }

        transport.Last.Should().NotBeNull();
        transport.Last!.IsSynthetic.Should().Be(synthetic);
    }

    // T-109 (REST 輸送): 合成なら `/search` の要求に標識が付き、通常は付かない。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task REST輸送は合成のときだけ標識を付ける(bool synthetic)
    {
        var handler = new CapturingHandler();
        var transport = new HttpRagSearchTransport(new SingleClientFactory(handler));

        await transport.SearchAsync(Query(synthetic), Ct);

        handler.LastPath.Should().Be("/search");
        handler.LastSynthetic.Should().Be(synthetic ? SyntheticTraffic.HeaderValue : null);
    }

    // T-109 (gRPC 輸送): 合成なら標識をメタデータで運び、通常はメタデータに何も足さない（利用者の資格情報も載せない）。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task gRPC輸送は合成のときだけ標識をメタデータで運ぶ(bool synthetic)
    {
        var client = new RecordingGrpcClient();
        var transport = new GrpcRagSearchTransport(client, NullLogger<GrpcRagSearchTransport>.Instance);

        await transport.SearchAsync(Query(synthetic), Ct);

        var headers = (client.LastOptions.Headers ?? []).ToList();
        if (synthetic)
            headers.Should().ContainSingle(h => h.Key == SyntheticTraffic.HeaderName.ToLowerInvariant()
                                               && h.Value == SyntheticTraffic.HeaderValue);
        else
            headers.Should().BeEmpty();
    }

    private sealed class RecordingTransport : IRagSearchTransport
    {
        public RagSearchQuery? Last { get; private set; }

        public Task<IReadOnlyList<SearchResultDto>> SearchAsync(RagSearchQuery query, CancellationToken ct)
        {
            Last = query;
            return Task.FromResult<IReadOnlyList<SearchResultDto>>([]);
        }
    }

    // `/authz/scope` は全許可、それ以外（LLM ゲートウェイ）は非 2xx。
    private sealed class ScopeOnlyFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new ScopeOnlyHandler()) { BaseAddress = new Uri("http://localhost/") };
    }

    private sealed class ScopeOnlyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/authz/scope"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(
                            new AccessScopeResponse("u", [], true),
                            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                        Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://retrieval/"),
        };
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        public string? LastSynthetic { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            LastSynthetic = request.Headers.TryGetValues(SyntheticTraffic.HeaderName, out var v)
                ? string.Join(",", v)
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"results\":[],\"totalCount\":0,\"elapsedMs\":0}",
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RecordingGrpcClient : Pb.DocumentSearch.DocumentSearchClient
    {
        public CallOptions LastOptions { get; private set; }

        public override AsyncUnaryCall<Pb.SearchResponse> SearchAsync(Pb.SearchRequest request, CallOptions options)
        {
            LastOptions = options;
            return new AsyncUnaryCall<Pb.SearchResponse>(
                Task.FromResult(new Pb.SearchResponse()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }
    }
}
