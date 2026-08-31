namespace ConversionService.Domain.Ports;

// FR-12, ADR-0012: 原本の本文を pandoc で Markdown 化するポート。
// 図（コード化候補）は本文とは別に抽出し、IDiagramCoder が PlantUML/Mermaid 化を試みる。
public interface IBodyConverter
{
    Task<BodyConversionResult> ConvertAsync(string storageUri, string contentType,
        CancellationToken ct = default);
}

// FR-12: pandoc による本文変換の結果。Markdown 本文と、原本から抽出した図の一覧を返す。
public record BodyConversionResult(string Markdown, IReadOnlyList<ExtractedFigure> Figures);

// FR-12, UC-06, ADR-0012, IADR-0320 (#1097): 変換器そのものが動かせない（pandoc が実行時イメージに
// 無い／原本を読み出せない）。**環境の欠陥であって原本の欠陥ではない**ため、再試行 → デッドレターへ
// 委ねる（ADR-0012「本文変換の恒久失敗は再試行し、継続失敗はデッドレターへ送る」）。
//
// 🔴 従前ここは例外ではなく**プレースホルダ本文（図0件）を返して成功**していた。
// 縮退は ConversionOptions.AllowDegradedBodyConversion が true のときだけに限る。
public sealed class BodyConversionUnavailableException(string message) : Exception(message);

// FR-12, UC-06, ADR-0012, IADR-0320 (#1097): 原本の形式が pandoc の**入力形式にならない**。
// 代表は PDF —— pandoc は PDF を出力にはできるが入力には取れない。
//
// 再試行しても結果は変わらないので、コンシューマは**再送出せず**恒久失敗として記録する
// （デッドレターへ流さず、変換ジョブ画面に理由の判る failed として出す）。
public sealed class UnsupportedSourceFormatException(string message) : Exception(message);

// FR-12, ADR-0012: 原本から抽出した図。IDiagramCoder が PlantUML/Mermaid 化を試み、
// 不可分（コード化不能・機密区分で送信不可）なものは画像として保持する。
public record ExtractedFigure(string FigureId, string ImageContentType, byte[] ImageBytes)
{
    // キャプション/近傍テキスト等、コード化のヒント（Vision 未対応時のプロンプト材料）。
    public string? Caption { get; init; }
}
