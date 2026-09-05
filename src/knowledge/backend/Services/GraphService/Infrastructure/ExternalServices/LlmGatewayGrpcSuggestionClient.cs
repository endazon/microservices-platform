using Grpc.Core;
using GraphService.Domain;
using GraphService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace GraphService.Infrastructure.ExternalServices;

// FR-18, FR-11, NFR-09, NFR-16, ADR-0010, ADR-0029, ADR-0034 決定 5, ADR-0075,
// IADR-0104, IADR-0266 決定 6・7, IADR-0379, IADR-0397, IADR-0398 (#1255):
// 提案生成の **east-west gRPC 経路**（REST の LlmGatewaySuggestionClient の兄弟）。
//
// **並走中の正は REST である。** 本クラスは `Services:LlmGatewayGrpc` が構成されたときだけ登録され
// （Program.cs）、無ければ従来の HTTP 実装がそのまま使われる。戻すのは構成を外すだけでよい。
//
// 🔴 **引数は SuggestionPrompt のみである**（ISuggestionLlmClient）。送信本文は封が組み立てる
// （Render）—— 組み立てを本クラスへ出すと、封を通っていない文字列を送る経路が開く。REST 実装と同じ。
//
// 🔴 **輸送の失敗は例外にせず `[]` へ落とす**（IADR-0398 決定 5）。REST 実装が
// 非 2xx・HttpRequestException を `[]` にしているのと**同じ枝**である ——
// **提案が 0 件になるだけで、越境も誤提案も起きない。**
// （埋め込みの呼び出し元とは向きが逆である。あちらは故障を「該当なし」に化けさせないため例外を上げるが、
//  提案は「付かない」が正しい縮退であり、利用者の要求を落とす理由にならない。）
public sealed class LlmGatewayGrpcSuggestionClient(
    Pb.LlmCompletion.LlmCompletionClient client,
    ILogger<LlmGatewayGrpcSuggestionClient> logger) : ISuggestionLlmClient
{
    public async Task<IReadOnlyList<LlmSuggestionProposal>> ProposeAsync(
        SuggestionPrompt prompt, CancellationToken ct = default)
    {
        CompletionApiResponse body;
        try
        {
            var resp = await client.CompleteAsync(
                LlmGrpcMapping.ToProto(new CompletionApiRequest(
                    prompt.Render(),
                    Confidentiality: prompt.Confidentiality,
                    Purpose: LlmGatewaySuggestionClient.PurposeName)),
                cancellationToken: ct);
            body = LlmGrpcMapping.ToDto(resp);
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            // RpcException（全 status）と s2s トークン取得失敗（InvalidOperationException）。
            // **呼び出し失敗を握り潰さず記録する**（REST 実装と同じ）。
            logger.LogWarning(ex, "LLM gateway gRPC call failed for suggestion generation");
            return [];
        }

        // IADR-0266 決定 6: **縮退した応答を根拠に使わない。**
        //   - Sent=false … 機密区分による送信拒否（越境させていない）
        //   - StopReason="refusal" … 送信は成立したがモデルが拒否した（ADR-0025 / IADR-0104）
        // どちらでも**提案を 1 件も作らない**。REST 実装と同じ判断である。
        //
        // 🔴 `Sent` は proto3 の既定（false）と DTO の既定（true）で向きが逆であり、
        // ゲートウェイが明示的に書いている（IADR-0398 決定 4）。写し漏れるとここで
        // **常に `[]` になる**（例外にならない）ため、GrpcSuggestionClientTests が対で固定する。
        if (!body.Sent || CompletionStopReasons.IsRefusal(body.StopReason))
            return [];

        return SuggestionProposalParser.Parse(body.Text, logger);
    }
}
