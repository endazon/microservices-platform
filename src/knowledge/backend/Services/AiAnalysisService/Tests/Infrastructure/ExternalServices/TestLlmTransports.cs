using System.Net;
using System.Text.Json;
using AiAnalysisService.Domain.Ports;
using AiAnalysisService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace AiAnalysisService.Tests.Infrastructure.ExternalServices;

// FR-04, FR-11, NFR-02, ADR-0029, ADR-0075, IADR-0379, IADR-0398 (#1255):
// 既存の RagOrchestrator 試験を **REST 輸送と gRPC 輸送の両方**で回すための小道具。
//
// 🔴 **同じ 1 つの元データから両輸送を組む。** 試験は従来どおり「ゲートウェイが返す本文」を
// JSON / SSE の文字列で書き、ここがそれを解釈して gRPC の偽クライアントへ載せ替える ——
// 元データを 2 つ書くと、**片方だけを直して「一致した」ことにできてしまう**。
//
// REST の側は `null` を返す（RagOrchestrator が `httpFactory` から HttpLlmCompletionTransport を組む。
// すなわち**現行そのまま**であり、既存試験の意味を変えていない）。
public enum LlmTransportKind
{
    Rest,
    Grpc,
}

public static class TestLlmTransports
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 試験が書いた「ゲートウェイの応答」から輸送を組む。
    /// <para>
    /// 🔴 <paramref name="status"/> が 2xx でないときの gRPC 側は <c>UNAVAILABLE</c> の
    /// <see cref="RpcException"/> にする —— gRPC には「非 2xx」に相当する概念が無く、
    /// 到達失敗も応答の失敗も等しく RpcException になるためである（IADR-0398 決定 5）。
    /// 呼び出し元が落ちる枝が REST と同じであることを、これで測る。
    /// </para>
    /// </summary>
    public static ILlmCompletionTransport? Create(
        LlmTransportKind kind,
        string llmBody,
        string mediaType = "application/json",
        HttpStatusCode status = HttpStatusCode.OK)
    {
        if (kind == LlmTransportKind.Rest)
            return null; // RagOrchestrator が httpFactory から REST 輸送を組む（現行のまま）。

        if (status != HttpStatusCode.OK)
            return Grpc(new ThrowingCompletionClient(
                new RpcException(new Status(StatusCode.Unavailable, "gateway unavailable"))));

        if (mediaType == "text/event-stream")
            return Grpc(new ReplayCompletionClient(streamEvents: ParseSse(llmBody)));

        var dto = JsonSerializer.Deserialize<CompletionApiResponse>(llmBody, Web);
        return Grpc(new ReplayCompletionClient(unary: dto));
    }

    private static GrpcLlmCompletionTransport Grpc(Pb.LlmCompletion.LlmCompletionClient client) =>
        new(client, NullLogger<GrpcLlmCompletionTransport>.Instance);

    // SSE の `data: ` 行を DTO の列へ戻す（試験が書いた本文をそのまま使うため）。
    private static List<CompletionStreamEvent> ParseSse(string sse)
    {
        var events = new List<CompletionStreamEvent>();
        foreach (var line in sse.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;
            var ev = JsonSerializer.Deserialize<CompletionStreamEvent>(line["data: ".Length..], Web);
            if (ev is not null)
                events.Add(ev);
        }
        return events;
    }

    // 生成クライアントのメソッドは virtual なので偽物を作れる（#1290 の GrpcEmbedTests と同じ手）。
    private sealed class ReplayCompletionClient(
        CompletionApiResponse? unary = null,
        List<CompletionStreamEvent>? streamEvents = null) : Pb.LlmCompletion.LlmCompletionClient
    {
        public override AsyncUnaryCall<Pb.CompleteResponse> CompleteAsync(
            Pb.CompleteRequest request, CallOptions options) =>
            new(Task.FromResult(LlmGrpcMapping.ToProto(
                    unary ?? new CompletionApiResponse(string.Empty, string.Empty, 0, 0))),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });

        public override AsyncServerStreamingCall<Pb.CompletionStreamEvent> CompleteStream(
            Pb.CompleteRequest request, CallOptions options) =>
            new(new ListStreamReader<Pb.CompletionStreamEvent>(
                    (streamEvents ?? []).Select(LlmGrpcMapping.ToProto).ToList()),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
    }

    private sealed class ThrowingCompletionClient(Exception exception) : Pb.LlmCompletion.LlmCompletionClient
    {
        public override AsyncUnaryCall<Pb.CompleteResponse> CompleteAsync(
            Pb.CompleteRequest request, CallOptions options) =>
            new(Task.FromException<Pb.CompleteResponse>(exception),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });

        // 🔴 **確立の時点で投げる。** REST の「送信失敗・非 2xx」に対応する枝であり、
        // 受信途中の失敗（別の文言へ落ちる）と区別するためにここで投げる必要がある。
        public override AsyncServerStreamingCall<Pb.CompletionStreamEvent> CompleteStream(
            Pb.CompleteRequest request, CallOptions options) => throw exception;
    }

    private sealed class ListStreamReader<T>(List<T> items) : IAsyncStreamReader<T>
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
