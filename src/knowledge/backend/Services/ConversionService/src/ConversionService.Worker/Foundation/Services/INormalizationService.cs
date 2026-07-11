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
public record NormalizationResult(
    Guid DocumentId,
    string MarkdownUri,
    IReadOnlyList<string> AssetUris,
    int DiagramsCoded,
    int DiagramsRetained);
