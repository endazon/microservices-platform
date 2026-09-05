using ConversionService.Domain.Ports;
using Grpc.Core;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace ConversionService.Infrastructure.ExternalServices;

// FR-12, FR-11, NFR-09, NFR-16, ADR-0010, ADR-0012, ADR-0029, ADR-0075,
// IADR-0104, IADR-0379, IADR-0397, IADR-0398 (#1255):
// 図のコード化の **east-west gRPC 経路**（REST の LlmGatewayDiagramCoder の兄弟）。
//
// **並走中の正は REST である。** 本クラスは `Services:LlmGatewayGrpc` が構成されたときだけ登録され
// （Program.cs）、無ければ従来の HTTP 実装がそのまま使われる。戻すのは構成を外すだけでよい。
//
// 🔴 **輸送の失敗は例外にせず `Retain("llm-call-failed")` へ落とす**（IADR-0398 決定 5）。
// REST 実装が `EnsureSuccessStatusCode` の例外と接続失敗を同じ理由で画像保持にしているのと
// **同じ枝・同じ理由文字列**である —— 変換パイプラインを止めないための deny-by-default であり、
// 理由コードが変わると運用の集計（何件がどの理由で画像保持になったか）が輸送で割れる。
public class LlmGatewayGrpcDiagramCoder(
    Pb.LlmCompletion.LlmCompletionClient client,
    ILogger<LlmGatewayGrpcDiagramCoder> logger) : IDiagramCoder
{
    public async Task<DiagramCodingResult> CodeAsync(ExtractedFigure figure, string? confidentiality,
        CancellationToken ct = default)
    {
        CompletionApiResponse result;
        try
        {
            var resp = await client.CompleteAsync(
                LlmGrpcMapping.ToProto(DiagramCodingInterpretation.BuildRequest(figure, confidentiality)),
                cancellationToken: ct);
            result = LlmGrpcMapping.ToDto(resp);
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            // RpcException（全 status）と s2s トークン取得失敗（InvalidOperationException）。
            // 🔴 **理由文字列は REST 実装と同じ `llm-call-failed` である**（変えると集計が輸送で割れる）。
            logger.LogWarning(ex, "Diagram coding gRPC call failed for {FigureId}; retaining as image",
                figure.FigureId);
            return DiagramCodingResult.Retain("llm-call-failed");
        }

        // 🔴 proto のメッセージは欠落しないため、gRPC 経路では `empty-response` は起こり得ない。
        // それでも共通の読み取りを通すのは、残る 4 経路（success / egress-denied / llm-refused /
        // not-codeable）の判断を輸送ごとに書き分けないためである。
        return DiagramCodingInterpretation.Interpret(result, figure, logger);
    }
}
