using System.Net.Http.Json;
using System.Text.Json;
using AiAnalysisService.Domain.Ports;
using AiAnalysisService.Infrastructure.ExternalServices;
using Grpc.Core;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Platform.Shared.Infrastructure.Foundation.Observability;
using AuthzPb = Platform.Shared.Contracts.Grpc.Authz.V1;
using LlmPb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace AiAnalysisService.Tests.Infrastructure.ExternalServices;

// FR-04, FR-05, NFR-09, NFR-16, ADR-0029, ADR-0075, 計画 ADR-0089 決定 1, [[IADR-0533]] (#1255):
// RagOrchestrator の試験で、**既存の HTTP スタブ（ゲートウェイ・認可・検索の応答を本文で書いたもの）を
// そのまま入力に使う**ための組み立て口。
//
// REST の並走を撤去したので、本番の輸送は gRPC だけである。試験は従来どおり「呼び出し先が返す本文」を
// JSON / SSE で書き、ここがそれを読んで次の形へ載せ替える:
//   - 認可（`/authz/scope`）→ 本物の `AuthzScopeGrpcClient` の下の生成クライアント（応答を proto へ写す。
//     非 2xx・不達は RpcException。ラッパの deny-by-default の枝をそのまま通す）
//   - 生成（`/complete`・`/complete/stream`）→ 本物の `GrpcLlmCompletionTransport` の下の生成クライアント
//     （非 2xx・不達は確立時の UNAVAILABLE。gRPC には「非 2xx」に相当する概念が無い。IADR-0400 決定 5）
//   - 検索（`/search`）→ `IRagSearchTransport` の替え玉（応答の本文を結果の列として返す。不達・非 2xx は 0 件）
//
// 🔴 **本番の縮退の枝を通すのは認可と生成である**（ラッパ・輸送は本物）。検索は替え玉であり、
// gRPC 輸送そのものの縮退は `GrpcRagSearchTransportTests` が測る。
public static class TestRagOrchestrator
{
    /// <summary>名前つき HttpClient の名前（スタブの振り分けに使う。旧 REST 実装の名前と同じ）。</summary>
    public const string AuthzClientName = "AuthorizationService";
    public const string RetrievalClientName = "RetrievalService";
    public const string LlmClientName = "LlmGateway";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static RagOrchestrator Create(
        IHttpClientFactory factory,
        IHttpContextAccessor? httpContextAccessor = null,
        ILogger<RagOrchestrator>? logger = null,
        IOptions<SyntheticMonitoringOptions>? syntheticOptions = null,
        ILlmCompletionTransport? completion = null,
        AuthzScopeGrpcClient? authz = null,
        IRagSearchTransport? search = null) =>
        new(
            completion ?? new GrpcLlmCompletionTransport(
                new HttpBackedCompletionClient(factory), NullLogger<GrpcLlmCompletionTransport>.Instance),
            search ?? new HttpBackedSearchTransport(factory),
            authz ?? AuthzOver(factory),
            httpContextAccessor,
            logger,
            syntheticOptions);

    /// <summary>`/authz/scope` の HTTP スタブを応答に使う、本物の <see cref="AuthzScopeGrpcClient"/>。</summary>
    public static AuthzScopeGrpcClient AuthzOver(IHttpClientFactory factory) => new(
        new AuthzPb.AuthzScope.AuthzScopeClient(new HttpBackedAuthzInvoker(factory)),
        NullLogger<AuthzScopeGrpcClient>.Instance);

    /// <summary>単一のハンドラを `/authz/scope` の HTTP スタブに使う形。</summary>
    public static AuthzScopeGrpcClient AuthzOver(HttpMessageHandler handler) =>
        AuthzOver(new SingleHandlerFactory(handler));

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
    }

    private static RpcException Unavailable(string why) => new(new Status(StatusCode.Unavailable, why));

    // 認可: `/authz/scope` の本文を proto へ写す（呼び出し先 `AuthzScopeGrpcService` と同じ写し）。
    private sealed class HttpBackedAuthzInvoker(IHttpClientFactory factory) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var task = ResolveAsync((AuthzPb.ResolveScopeRequest)(object)request!, options.CancellationToken)
                .ContinueWith(t => (TResponse)(object)t.GetAwaiter().GetResult(), TaskScheduler.Default);
            return new AsyncUnaryCall<TResponse>(task, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }

        private async Task<AuthzPb.ResolveScopeResponse> ResolveAsync(AuthzPb.ResolveScopeRequest req, CancellationToken ct)
        {
            AccessScopeResponse? body;
            try
            {
                var client = factory.CreateClient(AuthzClientName);
                using var resp = await client.PostAsJsonAsync("/authz/scope", new AccessScopeRequest(
                    req.UserId, new Dictionary<string, string>(req.UserAttributes), req.Action), ct);
                if (!resp.IsSuccessStatusCode)
                    throw Unavailable($"stub returned HTTP {(int)resp.StatusCode}");
                body = await resp.Content.ReadFromJsonAsync<AccessScopeResponse>(Web, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                throw Unavailable(ex.Message);
            }

            if (body is null)
                throw Unavailable("stub returned an empty body");
            return FakeAuthzScopeClient.ToProto(body);
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();
    }

    // 生成: `/complete`・`/complete/stream` の本文を proto へ写す。
    private sealed class HttpBackedCompletionClient(IHttpClientFactory factory) : LlmPb.LlmCompletion.LlmCompletionClient
    {
        public override AsyncUnaryCall<LlmPb.CompleteResponse> CompleteAsync(
            LlmPb.CompleteRequest request, CallOptions options) =>
            new(UnaryAsync(request, options.CancellationToken), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });

        private async Task<LlmPb.CompleteResponse> UnaryAsync(LlmPb.CompleteRequest request, CancellationToken ct)
        {
            try
            {
                var client = factory.CreateClient(LlmClientName);
                using var resp = await client.PostAsJsonAsync("/complete", LlmGrpcMapping.ToDto(request), ct);
                if (!resp.IsSuccessStatusCode)
                    throw Unavailable($"stub returned HTTP {(int)resp.StatusCode}");
                var dto = await resp.Content.ReadFromJsonAsync<CompletionApiResponse>(Web, ct);
                // proto には null が無い。本文が JSON の null なら既定値の応答（空の本文・空のモデル）になる。
                return LlmGrpcMapping.ToProto(dto ?? new CompletionApiResponse(string.Empty, string.Empty, 0, 0));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw Unavailable(ex.Message);
            }
        }

        // 🔴 **確立の時点で投げる**（送信失敗・非 2xx）。本文は読み切ってから 1 件ずつ返す。
        public override AsyncServerStreamingCall<LlmPb.CompletionStreamEvent> CompleteStream(
            LlmPb.CompleteRequest request, CallOptions options)
        {
            var events = StreamBodyAsync(request, options.CancellationToken).GetAwaiter().GetResult();
            return new AsyncServerStreamingCall<LlmPb.CompletionStreamEvent>(
                new ListReader<LlmPb.CompletionStreamEvent>(events.Select(LlmGrpcMapping.ToProto).ToList()),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
        }

        private async Task<List<CompletionStreamEvent>> StreamBodyAsync(LlmPb.CompleteRequest request, CancellationToken ct)
        {
            string body;
            try
            {
                var client = factory.CreateClient(LlmClientName);
                using var resp = await client.PostAsJsonAsync("/complete/stream", LlmGrpcMapping.ToDto(request), ct);
                if (!resp.IsSuccessStatusCode)
                    throw Unavailable($"stub returned HTTP {(int)resp.StatusCode}");
                body = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw Unavailable(ex.Message);
            }

            var events = new List<CompletionStreamEvent>();
            foreach (var line in body.Split('\n'))
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                    continue;
                try
                {
                    if (JsonSerializer.Deserialize<CompletionStreamEvent>(line["data: ".Length..], Web) is { } ev)
                        events.Add(ev);
                }
                catch (JsonException)
                {
                    // 壊れた行は無視する（旧 REST 輸送の読み取りと同じ扱い）。
                }
            }
            return events;
        }
    }

    // 検索: `/search` の応答本文を結果の列として返す替え玉。要求本文は呼び出し先と同じ形（スコープを含む）で送る。
    private sealed class HttpBackedSearchTransport(IHttpClientFactory factory) : IRagSearchTransport
    {
        public async Task<IReadOnlyList<SearchResultDto>> SearchAsync(RagSearchQuery query, CancellationToken ct)
        {
            try
            {
                var client = factory.CreateClient(RetrievalClientName);
                using var resp = await client.PostAsJsonAsync("/search",
                    new SearchRequest(query.Query, query.TopK, null, query.EffectiveScope), ct);
                if (!resp.IsSuccessStatusCode)
                    return [];
                return (await resp.Content.ReadFromJsonAsync<SearchResponse>(Web, ct))?.Results ?? [];
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return [];
            }
        }
    }

    private sealed class ListReader<T>(List<T> items) : IAsyncStreamReader<T>
    {
        private int _index = -1;
        public T Current => items[_index];

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            _index++;
            return Task.FromResult(_index < items.Count);
        }
    }
}
