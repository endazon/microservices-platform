using ConversionService.Worker.Foundation.Ports;
using ConversionService.Worker.Foundation.Domain;
using System.Text;
using Knowledge.Contracts.Events;

namespace ConversionService.Worker.Foundation.Services;

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
            }
            else
            {
                // 不可分: 画像をオブジェクトストレージへ保持し、本文へ参照を埋め込む（段階的コード化）。
                var key = $"{documentId:N}/assets/{figure.FigureId}{ExtensionFor(figure.ImageContentType)}";
                var uri = await objectStore.SaveAssetAsync(key, figure.ImageBytes, figure.ImageContentType, ct);
                assetUris.Add(uri);
                markdown.Append("\n\n![").Append(figure.FigureId).Append("](").Append(uri).Append(")\n");
                retained++;
            }
        }

        // 3. 正規化 Markdown をオブジェクトストレージへ保管する。
        var markdownUri = await objectStore.SaveMarkdownAsync($"{documentId:N}/document.md",
            markdown.ToString(), ct);

        logger.LogInformation(
            "Normalized {DocumentId}: diagrams coded={Coded} retained={Retained} assets={Assets}",
            documentId, coded, retained, assetUris.Count);

        return new NormalizationResult(documentId, markdownUri, assetUris, coded, retained);
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
