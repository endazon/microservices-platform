namespace ConversionService.Domain.Ports;

// FR-12, ADR-0012: 原本の本文を pandoc で Markdown 化するポート。
// 図（コード化候補）は本文とは別に抽出し、IDiagramCoder が PlantUML/Mermaid 化を試みる。
public interface IBodyConverter
{
    Task<BodyConversionResult> ConvertAsync(string storageUri, string contentType,
        CancellationToken ct = default);
}

// FR-12: 本文変換の結果。Markdown 本文と、原本から抽出した図の一覧を返す。
public record BodyConversionResult(string Markdown, IReadOnlyList<ExtractedFigure> Figures)
{
    // FR-12, UC-06, SC-07, ADR-0070 決定 3, IADR-0356 (#1192), [[IADR-0381]] (#1254):
    // **原本が本文を持っていたか。** `false` は「本文が存在しない」（テキスト層を持たない PDF）で、
    // 抽出結果が空白のみであることを確かめたうえで倒す。
    //
    // 🔴 これは失敗ではない —— 再試行しても結果は変わらず、デッドレターに溜める価値も無い。
    // 変換は「本文なし・原本参照のみ」として**完了**し、ジョブは succeeded の内訳として画面へ出る。
    // 本文そのものが作れない失敗（抽出器の不在・非 0 終了）は従来どおり例外で表す。
    //
    // **既定は `true`（本文あり）。** 明示しない変換器（pandoc 経路）は従来どおり本文ありを返す。
    public bool HasBody { get; init; } = true;
}

// FR-12, UC-06, ADR-0012, IADR-0320 (#1097): 変換器そのものが動かせない（pandoc が実行時イメージに
// 無い／原本を読み出せない）。**環境の欠陥であって原本の欠陥ではない**ため、再試行 → デッドレターへ
// 委ねる（ADR-0012「本文変換の恒久失敗は再試行し、継続失敗はデッドレターへ送る」）。
//
// 🔴 従前ここは例外ではなく**プレースホルダ本文（図0件）を返して成功**していた。
// 縮退は ConversionOptions.AllowDegradedBodyConversion が true のときだけに限る。
public sealed class BodyConversionUnavailableException(string message) : Exception(message);

// FR-12, UC-06, ADR-0012, ADR-0070 決定 5, IADR-0320 (#1097), IADR-0356 (#1192): 原本の形式が
// **どの変換器の入力にもならない**（計画の対応形式表に無い未知の形式）。
//
// 🔴 従前の代表は PDF だったが、ADR-0070 決定 2 により PDF はテキスト層の抽出器へ振り分ける
// （`FormatRoutingBodyConverter`）。**PDF はもうこの例外にならない。** 残るのは未知の MIME ＋未知の拡張子
// —— 従前は既定の `markdown` へ落として pandoc に食わせていたが、対応していない形式が**静かに壊れた本文**
// になる（ADR-0070 決定 5）ため、取り寄せる前に拒否する。
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
