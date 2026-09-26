using ConversionService.Domain.Ports;
using System.Net.Http.Json;
using Platform.Shared.Contracts.Dtos;

namespace ConversionService.Infrastructure.ExternalServices;

// FR-12, ADR-0012, ADR-0010: 図を LLMゲートウェイ /complete 経由で PlantUML/Mermaid にコード化する。
// 変換時の LLM 呼び出しも機密区分（confidentiality）で送信制御する（FR-11 の越境マトリクスへ委譲）。
// 送信拒否（Sent=false）・コード化不能・呼び出し失敗はいずれも「画像として保持」へ収束させる（deny-by-default）。
//
// IADR-0400 (#1255): プロンプトの組み立てと応答の読み取りは DiagramCodingInterpretation にある
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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 呼び出し失敗は例外送出せず画像保持へ縮退する（変換パイプラインを止めない）。
            // 🔴 UC-06 (#1621): **時間切れも呼び出し失敗である。** `HttpClient.Timeout` の経過は
            // `TaskCanceledException`（`OperationCanceledException` の派生）で表れ、呼び出し元の ct は立っていない。
            // 型だけで絞ると、LLM ゲートウェイの時間切れ 1 回で `RawDocumentFetchedConsumer` の正規化全体が
            // 失敗していた。外へ出す取り消しは**呼び出し元（メッセージ消費）の ct によるもの**だけである。
            logger.LogWarning(ex, "Diagram coding call failed for {FigureId}; retaining as image", figure.FigureId);
            return DiagramCodingResult.Retain("llm-call-failed");
        }

        return DiagramCodingInterpretation.Interpret(result, figure, logger);
    }
}
