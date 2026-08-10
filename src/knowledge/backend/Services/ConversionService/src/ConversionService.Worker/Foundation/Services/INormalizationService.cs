using ConversionService.Worker.Foundation.Ports;
using ConversionService.Worker.Foundation.Domain;
using Knowledge.Contracts.Events;

namespace ConversionService.Worker.Foundation.Services;

// FR-12, UC-06: 原本を正規化形式（本文Markdown＋資産）へ変換するオーケストレータ。
// 本文変換（pandoc）・図のコード化（LLM）・画像保持・オブジェクトストレージ保管を束ねる。
public interface INormalizationService
{
    Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw, CancellationToken ct = default);
}

// FR-12: 正規化結果。冪等な DocumentId、Markdown 参照 URI と資産（保持した画像）URI、
// コード化/保持した図数。DocumentId は SourceId＋原本パスから決定的に導出される（再変換で同一）。
//
// IADR-0154 決定 1: **図 1 つ 1 つの記録（Figures）も返す。** 従前は件数だけを返し、呼び出し側
// （RawDocumentFetchedConsumer）がログへ出して捨てていたため、「どの図が画像保持へ縮退したか」を
// 後から問い合わせる手段が無かった —— 人手補正 Phase 1 はまさにその図を対象とする。
public record NormalizationResult(
    Guid DocumentId,
    string MarkdownUri,
    IReadOnlyList<string> AssetUris,
    int DiagramsCoded,
    int DiagramsRetained,
    IReadOnlyList<NormalizedFigure> Figures);

// FR-12, UC-06, IADR-0154: 抽出した図 1 つ分の記録。Coded == false が人手補正の対象である。
public record NormalizedFigure(
    string FigureId,
    bool Coded,
    string? Language,
    string? Code,
    string? ImageUri,
    string? ImageContentType,
    string? Caption);
