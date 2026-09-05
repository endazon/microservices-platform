using System.Text.RegularExpressions;
using ConversionService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;

namespace ConversionService.Infrastructure.ExternalServices;

// FR-12, FR-11, ADR-0010, ADR-0012, ADR-0025, IADR-0104, IADR-0398 (#1255):
// 図のコード化における**プロンプトの組み立てと応答の読み取り**。
//
// 🔴 REST 実装（LlmGatewayDiagramCoder）と gRPC 実装（LlmGatewayGrpcDiagramCoder）が
// **同じここを呼ぶ**。4 つの帰結（success / egress-denied / llm-refused / not-codeable）を
// 輸送ごとに書くと、**どちらか一方だけが refusal を「コード化不能」と誤記録する**
// （IADR-0104 が名指しした形）ため、判断を 1 か所に閉じる。
internal static partial class DiagramCodingInterpretation
{
    // 応答から ```mermaid / ```plantuml のフェンス済みコードブロックを取り出す。
    [GeneratedRegex(@"```(mermaid|plantuml)\s*\n(.*?)```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FencedCodeBlock();

    // 図のコード化に使う上限。REST 実装が従来から明示している値をそのまま持つ
    // （proto3 では 0 が「未指定」になるため、明示値であることに意味がある。IADR-0398 決定 4）。
    public const int MaxTokens = 1024;

    // 監査・課金集計で用途が識別できるようにする（ゲートウェイ側は自由文字列として扱う）。
    public const string PurposeName = "diagram-coding";

    // Vision 対応は /complete のマルチモーダル拡張後のフォローアップ。現時点はキャプション等を材料に依頼する。
    public static CompletionApiRequest BuildRequest(ExtractedFigure figure, string? confidentiality)
    {
        var prompt =
            "次の図を Mermaid か PlantUML のコードに変換せよ。可能なら ```mermaid か ```plantuml の" +
            "フェンス付きコードブロックだけを出力し、コード化できない場合は「不可」とだけ答えよ。\n" +
            $"図ID: {figure.FigureId}\n説明: {figure.Caption ?? "(なし)"}";

        // ADR-0010/0012: confidentiality を渡し、purpose=diagram-coding で送信先を切り替える。
        return new CompletionApiRequest(
            Prompt: prompt,
            MaxTokens: MaxTokens,
            Confidentiality: confidentiality,
            Purpose: PurposeName);
    }

    // ゲートウェイの応答を 4 つの帰結へ落とす。**画像保持へ収束させる点（deny-by-default）は不変**で、
    // 理由だけを正しく残す。
    public static DiagramCodingResult Interpret(
        CompletionApiResponse? result, ExtractedFigure figure, ILogger logger)
    {
        if (result is null)
            return DiagramCodingResult.Retain("empty-response");

        // 機密区分により送信拒否（Sent=false）→ 画像として保持する（ADR-0012 の機密制御）。
        //
        // 🔴 `Sent` は proto3 の既定（false）と DTO の既定（true）で向きが逆であり、ゲートウェイが
        // 明示的に書いている（IADR-0398 決定 4）。写し漏れると gRPC 経路で**全ての図が
        // egress-denied として画像保持になる**（例外にならない）。GrpcDiagramCoderTests が対で固定する。
        if (!result.Sent)
        {
            logger.LogInformation("Diagram {FigureId} not sent to LLM (egress denied): {Reason}; retaining as image",
                figure.FigureId, result.RoutingReason);
            return DiagramCodingResult.Retain($"egress-denied:{result.RoutingReason}");
        }

        // FR-11, IADR-0104 (#379): 送信は成立したがモデルが拒否した場合（stopReason="refusal"）。
        // 本文は空で返るためフェンス無しとなり、区別しないと「コード化不能」として記録され原因を見失う。
        if (CompletionStopReasons.IsRefusal(result.StopReason))
        {
            logger.LogWarning("Diagram {FigureId} was refused by the model (stop_reason=refusal); retaining as image",
                figure.FigureId);
            return DiagramCodingResult.Retain("llm-refused");
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
