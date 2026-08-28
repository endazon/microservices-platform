namespace ConversionService.Worker.Domain.Ports;

// FR-12, ADR-0012: 原本の本文を pandoc で Markdown 化するポート。
// 図（コード化候補）は本文とは別に抽出し、IDiagramCoder が PlantUML/Mermaid 化を試みる。
public interface IBodyConverter
{
    Task<BodyConversionResult> ConvertAsync(string storageUri, string contentType,
        CancellationToken ct = default);
}

// FR-12: pandoc による本文変換の結果。Markdown 本文と、原本から抽出した図の一覧を返す。
public record BodyConversionResult(string Markdown, IReadOnlyList<ExtractedFigure> Figures);

// FR-12, ADR-0012: 原本から抽出した図。IDiagramCoder が PlantUML/Mermaid 化を試み、
// 不可分（コード化不能・機密区分で送信不可）なものは画像として保持する。
public record ExtractedFigure(string FigureId, string ImageContentType, byte[] ImageBytes)
{
    // キャプション/近傍テキスト等、コード化のヒント（Vision 未対応時のプロンプト材料）。
    public string? Caption { get; init; }
}
