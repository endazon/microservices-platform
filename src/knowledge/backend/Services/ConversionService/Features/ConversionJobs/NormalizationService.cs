using ConversionService.Domain.Ports;
using ConversionService.Domain;
using System.Text;
using Knowledge.Contracts.Events;

namespace ConversionService.Features.ConversionJobs;

// FR-12, UC-06, ADR-0012/0014: 原本を正規化形式（本文Markdown＋資産）へ変換するオーケストレータ。
// 本文は pandoc で Markdown 化し、図は LLM で PlantUML/Mermaid 化する。コード化できない図は
// 画像としてオブジェクトストレージへ保持し、本文へ参照を埋め込む。
public class NormalizationService(
    IBodyConverter bodyConverter,
    IDiagramCoder diagramCoder,
    IObjectStore objectStore,
    ILogger<NormalizationService> logger) : INormalizationService
{
    public async Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw,
        CancellationToken ct = default)
    {
        // 文書の機密区分（ABAC confidentiality）。図のコード化で LLM へ送る際の送信制御に使う。
        raw.Attributes.TryGetValue("confidentiality", out var confidentiality);

        // 1. 本文を pandoc で Markdown 化し、図を抽出する。
        var body = await bodyConverter.ConvertAsync(raw.StorageUri, raw.ContentType, ct);

        var markdown = new StringBuilder(body.Markdown);
        var assetUris = new List<string>();
        // IADR-0154 決定 1: 図 1 つ 1 つを記録して返す（人手補正 Phase 1 の対象を後から引けるように）。
        var figures = new List<NormalizedFigure>();
        var coded = 0;
        var retained = 0;

        // 冪等性: DocumentId は SourceId＋原本パスから決定的に導出する（再変換で同一 ID）。
        var documentId = DeterministicGuid.ForDocument(raw.SourceId, raw.OriginalPath);

        // 2. 図ごとにコード化を試み、成功はコードブロック埋込・不可は画像保持へ振り分ける。
        foreach (var figure in body.Figures)
        {
            var result = await diagramCoder.CodeAsync(figure, confidentiality, ct);
            if (result.Coded)
            {
                // コード化成功: PlantUML/Mermaid をコードブロックとして本文へ埋め込む。
                markdown.Append("\n\n```").Append(result.Language).Append('\n')
                    .Append(result.Code).Append("\n```\n");
                coded++;
                figures.Add(new NormalizedFigure(figure.FigureId, true, result.Language, result.Code,
                    null, null, figure.Caption));
            }
            else
            {
                // 不可分: 画像をオブジェクトストレージへ保持し、本文へ参照を埋め込む（段階的コード化）。
                var key = $"{documentId:N}/assets/{figure.FigureId}{ExtensionFor(figure.ImageContentType)}";
                var uri = await objectStore.SaveAssetAsync(key, figure.ImageBytes, figure.ImageContentType, ct);
                assetUris.Add(uri);
                // UC-06 / IADR-0154 決定 3: この埋め込み形は人手補正が置換する目印でもある。
                // 形を変えるときは FigureMarkdown.ImageEmbed と一緒に変えること。
                markdown.Append("\n\n").Append(FigureMarkdown.ImageEmbed(figure.FigureId, uri)).Append('\n');
                retained++;
                figures.Add(new NormalizedFigure(figure.FigureId, false, null, null,
                    uri, figure.ImageContentType, figure.Caption));
            }
        }

        // 3. 正規化 Markdown をオブジェクトストレージへ保管する。
        var markdownUri = await objectStore.SaveMarkdownAsync($"{documentId:N}/document.md",
            markdown.ToString(), ct);

        logger.LogInformation(
            "Normalized {DocumentId}: diagrams coded={Coded} retained={Retained} assets={Assets}",
            documentId, coded, retained, assetUris.Count);

        return new NormalizationResult(documentId, markdownUri, assetUris, coded, retained, figures);
    }

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/svg+xml" => ".svg",
        "image/webp" => ".webp",
        _ => ".bin"
    };
}
