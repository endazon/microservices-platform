using ConversionService.Worker.Foundation.Ports;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace ConversionService.Worker.Composable.Adapters;

// FR-12, ADR-0012, ADR-0010: 図を LLMゲートウェイ /complete 経由で PlantUML/Mermaid にコード化する。
// 変換時の LLM 呼び出しも機密区分（confidentiality）で送信制御する（FR-11 の越境マトリクスへ委譲）。
// 送信拒否（Sent=false）・コード化不能・呼び出し失敗はいずれも「画像として保持」へ収束させる（deny-by-default）。
public partial class LlmGatewayDiagramCoder(
    HttpClient http,
    ILogger<LlmGatewayDiagramCoder> logger) : IDiagramCoder
{
    // 応答から ```mermaid / ```plantuml のフェンス済みコードブロックを取り出す。
    [GeneratedRegex(@"```(mermaid|plantuml)\s*\n(.*?)```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FencedCodeBlock();

    public async Task<DiagramCodingResult> CodeAsync(ExtractedFigure figure, string? confidentiality,
        CancellationToken ct = default)
    {
        // Vision 対応は /complete のマルチモーダル拡張後のフォローアップ。現時点はキャプション等を材料に依頼する。
        var prompt =
            "次の図を Mermaid か PlantUML のコードに変換せよ。可能なら ```mermaid か ```plantuml の" +
            "フェンス付きコードブロックだけを出力し、コード化できない場合は「不可」とだけ答えよ。\n" +
            $"図ID: {figure.FigureId}\n説明: {figure.Caption ?? "(なし)"}";

        CompletionApiResponse? result;
        try
        {
            // ADR-0010/0012: confidentiality を渡し、purpose=diagram-coding で送信先を切り替える。
            var resp = await http.PostAsJsonAsync("/complete", new CompletionApiRequest(
                Prompt: prompt,
                MaxTokens: 1024,
                Confidentiality: confidentiality,
                Purpose: "diagram-coding"), ct);
            resp.EnsureSuccessStatusCode();
            result = await resp.Content.ReadFromJsonAsync<CompletionApiResponse>(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 呼び出し失敗は例外送出せず画像保持へ縮退する（変換パイプラインを止めない）。
            logger.LogWarning(ex, "Diagram coding call failed for {FigureId}; retaining as image", figure.FigureId);
            return DiagramCodingResult.Retain("llm-call-failed");
        }

        if (result is null)
            return DiagramCodingResult.Retain("empty-response");

        // 機密区分により送信拒否（Sent=false）→ 画像として保持する（ADR-0012 の機密制御）。
        if (!result.Sent)
        {
            logger.LogInformation("Diagram {FigureId} not sent to LLM (egress denied): {Reason}; retaining as image",
                figure.FigureId, result.RoutingReason);
            return DiagramCodingResult.Retain($"egress-denied:{result.RoutingReason}");
        }

        var match = FencedCodeBlock().Match(result.Text ?? string.Empty);
        if (!match.Success)
        {
            // コード化不能（「不可」等）→ 段階的コード化方針に従い画像として保持する。
            logger.LogInformation("Diagram {FigureId} could not be coded; retaining as image", figure.FigureId);
            return DiagramCodingResult.Retain("not-codeable");
        }

        var language = match.Groups[1].Value.ToLowerInvariant();
        var code = match.Groups[2].Value.Trim();
        logger.LogInformation("Diagram {FigureId} coded as {Language}", figure.FigureId, language);
        return DiagramCodingResult.Success(language, code);
    }
}
