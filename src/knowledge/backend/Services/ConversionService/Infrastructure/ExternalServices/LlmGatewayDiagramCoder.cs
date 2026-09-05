using ConversionService.Domain.Ports;
using System.Net.Http.Json;
using Platform.Shared.Contracts.Dtos;

namespace ConversionService.Infrastructure.ExternalServices;

// FR-12, ADR-0012, ADR-0010: 図を LLMゲートウェイ /complete 経由で PlantUML/Mermaid にコード化する。
// 変換時の LLM 呼び出しも機密区分（confidentiality）で送信制御する（FR-11 の越境マトリクスへ委譲）。
// 送信拒否（Sent=false）・コード化不能・呼び出し失敗はいずれも「画像として保持」へ収束させる（deny-by-default）。
//
// IADR-0398 (#1255): プロンプトの組み立てと応答の読み取りは DiagramCodingInterpretation にある
// （gRPC 実装が同じものを呼ぶ。輸送ごとに 4 つの帰結を書き分けない）。
// **本クラスに残るのは REST 輸送と、その失敗を画像保持へ落とす枝だけである。**
public class LlmGatewayDiagramCoder(
    HttpClient http,
    ILogger<LlmGatewayDiagramCoder> logger) : IDiagramCoder
{
    public async Task<DiagramCodingResult> CodeAsync(ExtractedFigure figure, string? confidentiality,
        CancellationToken ct = default)
    {
        CompletionApiResponse? result;
        try
        {
            var resp = await http.PostAsJsonAsync(
                "/complete", DiagramCodingInterpretation.BuildRequest(figure, confidentiality), ct);
            resp.EnsureSuccessStatusCode();
            result = await resp.Content.ReadFromJsonAsync<CompletionApiResponse>(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 呼び出し失敗は例外送出せず画像保持へ縮退する（変換パイプラインを止めない）。
            logger.LogWarning(ex, "Diagram coding call failed for {FigureId}; retaining as image", figure.FigureId);
            return DiagramCodingResult.Retain("llm-call-failed");
        }

        return DiagramCodingInterpretation.Interpret(result, figure, logger);
    }
}
