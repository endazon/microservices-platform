using System.Runtime.CompilerServices;
using AiAnalysisService.Domain.Ports;
using Grpc.Core;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Platform.Shared.Infrastructure.Foundation.Observability;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace AiAnalysisService.Infrastructure.ExternalServices;

// FR-04, FR-11, NFR-02, NFR-09, NFR-16, ADR-0010, ADR-0029, ADR-0044, ADR-0075, ADR-0076 決定 4・5,
// IADR-0354, IADR-0378, IADR-0379, IADR-0397, IADR-0398 (#1255): テキスト生成の **east-west gRPC 輸送**。
//
// **並走中の正は REST である。** 本クラスは `Services:LlmGatewayGrpc` が構成されたときだけ登録され
// （Program.cs）、無ければ HttpLlmCompletionTransport がそのまま使われる。戻すのは構成を外すだけでよい。
//
// 🔴 **`CompleteStream` はサーバストリーミングであり、届いた 1 メッセージをその場で yield する**
// （IADR-0398 決定 1）。ここで溜めると、gRPC 側で server-streaming を選んだ意味が消え、
// NFR-02 の SLI（`rag.answer.first_token.duration`。IADR-0354）の終点が生成完了時刻まで遅れる。
//
// 🔴 **輸送の失敗を例外のまま上げない。埋め込みの呼び出し元とは向きが逆である**（IADR-0398 決定 5）。
// 埋め込み（IADR-0397 決定 4）は `RpcException` を上げるが、生成は上げない —— REST 実装が
// SSE で `done(Sent=false)` を返し 500 を伝播させていないからであり、**移行の不変条件は
// 「挙動を変えない」**である。上げてしまうと、現在は縮退表示になる場面が north-south の 500 になる。
public sealed class GrpcLlmCompletionTransport(
    Pb.LlmCompletion.LlmCompletionClient client,
    ILogger<GrpcLlmCompletionTransport> logger) : ILlmCompletionTransport
{
    public async IAsyncEnumerable<CompletionStreamEvent> StreamAsync(
        CompletionApiRequest body, bool isSynthetic, [EnumeratorCancellation] CancellationToken ct)
    {
        // ADR-0044, ADR-0076 決定 4, IADR-0398 決定 3: 標識は**メタデータ**で運ぶ（本文に載せない）。
        var headers = new Metadata();
        SyntheticTraffic.PropagateTo(headers, isSynthetic);

        // 🔴 呼び出しの**確立**の失敗（s2s トークン取得失敗を含む）は、REST の
        // 「送信失敗・非 2xx」と同じ枝へ落とす。
        AsyncServerStreamingCall<Pb.CompletionStreamEvent>? call = null;
        var startFaulted = false;
        try
        {
            call = client.CompleteStream(LlmGrpcMapping.ToProto(body), headers, cancellationToken: ct);
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            logger.LogWarning(ex, "LLM gateway gRPC stream could not be started");
            startFaulted = true;
        }

        if (startFaulted || call is null)
        {
            call?.Dispose();
            yield return new CompletionStreamEvent(string.Empty, Done: true, Sent: false,
                Text: "LLM が現在利用できません。");
            yield break;
        }

        using (call)
        {
            var responses = call.ResponseStream;
            while (true)
            {
                var readFaulted = false;
                try
                {
                    if (!await responses.MoveNext(ct))
                        yield break; // ストリーム終端
                }
                catch (Exception ex) when (IsTransportFailure(ex, ct))
                {
                    // 🔴 受信途中の失敗は REST の「読み取り中断」と同じ枝である。
                    // **確立の失敗と文言を分ける** —— 分けないと、呼び出し元が「一度も届かなかった」と
                    // 「途中で切れた」を区別できなくなる（現行 REST 実装が区別している）。
                    logger.LogWarning(ex, "LLM gateway gRPC stream failed while reading");
                    readFaulted = true;
                }

                if (readFaulted)
                {
                    yield return new CompletionStreamEvent(string.Empty, Done: true, Sent: false,
                        Text: "LLM 応答の受信に失敗しました。");
                    yield break;
                }

                yield return LlmGrpcMapping.ToDto(responses.Current);
            }
        }
    }

    public async Task<LlmCompletionOutcome> CompleteAsync(
        CompletionApiRequest body, bool isSynthetic, CancellationToken ct)
    {
        var headers = new Metadata();
        SyntheticTraffic.PropagateTo(headers, isSynthetic);

        try
        {
            var resp = await client.CompleteAsync(
                LlmGrpcMapping.ToProto(body), headers, cancellationToken: ct);
            // 🔴 proto のメッセージは欠落しない。したがって gRPC 経路では
            // 「応答は得たが本文を復元できなかった」（Answered(null)）は起こり得ない。
            return LlmCompletionOutcome.Answered(LlmGrpcMapping.ToDto(resp));
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            // 🔴 REST の**非 2xx と同じ枝**（出典のみ返す）へ落とす。
            // gRPC には「非 2xx」に相当する概念が無く、到達失敗も応答の失敗も等しく RpcException に
            // なるため、REST が例外として伝播させる「接続失敗」もここでは縮退へ倒れる ——
            // **観測できる縮退は一致し、gRPC の側が緩い方向**である（IADR-0398 決定 5・作業仕様書 §計画書との差異）。
            logger.LogWarning(ex, "LLM gateway gRPC completion call failed");
            return LlmCompletionOutcome.NotReached();
        }
    }

    // 🔴 縮退させるのは**輸送と s2s の失敗だけ**である。
    // `RpcException`（全 status）と s2s トークン取得失敗（ClientCredentialsServiceTokenProvider が投げる
    // `InvalidOperationException`）を捕まえ、`OperationCanceledException`（利用者による中断）と
    // それ以外の予期しない例外は**そのまま上げる** —— 何でも縮退させると実装の誤りが
    // 「LLM が利用できません」に化けて見えなくなる。
    private static bool IsTransportFailure(Exception ex, CancellationToken ct) =>
        ex is RpcException or InvalidOperationException && !ct.IsCancellationRequested;
}
