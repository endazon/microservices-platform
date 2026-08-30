namespace ConversionService.Domain.Ports;

// FR-12, ADR-0012, ADR-0010: 図を LLM（LLMゲートウェイ経由）で PlantUML/Mermaid にコード化するポート。
// 変換時の LLM 呼び出しも文書の機密区分に応じて送信制御する（confidentiality を渡す）。
public interface IDiagramCoder
{
    Task<DiagramCodingResult> CodeAsync(ExtractedFigure figure, string? confidentiality,
        CancellationToken ct = default);
}

// FR-12: 図のコード化結果。
//   Coded=true  … PlantUML/Mermaid 化に成功（Language / Code を本文へ埋め込む）。
//   Coded=false … コード化不能・機密区分で送信拒否・呼び出し失敗のいずれか（画像として保持する）。
public record DiagramCodingResult(bool Coded, string? Language = null, string? Code = null,
    string? Reason = null)
{
    public static DiagramCodingResult Success(string language, string code)
        => new(true, Language: language, Code: code);

    // 段階的に全面コード化を目指す方針（ADR-0012）に従い、不可分は画像保持へ収束させる。
    public static DiagramCodingResult Retain(string reason) => new(false, Reason: reason);
}
