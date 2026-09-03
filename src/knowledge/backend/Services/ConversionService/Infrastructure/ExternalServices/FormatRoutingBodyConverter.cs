using ConversionService.Domain.Ports;

namespace ConversionService.Infrastructure.ExternalServices;

// FR-12, UC-06, ADR-0012, ADR-0070 決定 2・5, IADR-0356 決定 2 (#1192): 原本の形式で本文変換器を振り分ける。
//
// - PDF → `PdfTextLayerConverter`（テキスト層の抽出器。ADR-0070 決定 2）
// - それ以外 → `PandocConversionService`（ADR-0012「本文は pandoc」は PDF 以外でそのまま有効）
// - どちらの入力にもならない未知の形式 → `UnsupportedSourceFormatException`（取り寄せる前に拒否する）
//
// `NormalizationService` は `IBodyConverter` しか知らない。IADR-0008 が置いた 3 ポートの境界は不変で、
// ゴールデン（IADR-0298）の差し替え点もそのままである。
//
// 🔴 **形式の判定は `PandocConversionService.PandocInputFormat` の 1 箇所で行う**（写像表を 2 箇所へ持たない）。
// 同関数は PDF で `null`（＝ pandoc の担当ではない）を返し、未知の形式で例外を投げる。
public sealed class FormatRoutingBodyConverter(
    PandocConversionService pandoc,
    PdfTextLayerConverter pdfTextLayer) : IBodyConverter
{
    public Task<BodyConversionResult> ConvertAsync(string storageUri, string contentType,
        CancellationToken ct = default)
    {
        var pandocFormat = PandocConversionService.PandocInputFormat(contentType, storageUri);
        return pandocFormat is null
            ? pdfTextLayer.ConvertAsync(storageUri, contentType, ct)
            : pandoc.ConvertAsync(storageUri, contentType, ct);
    }
}
