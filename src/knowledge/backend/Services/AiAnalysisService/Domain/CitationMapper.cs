using Knowledge.Contracts.Dtos;
using System.Text;

namespace AiAnalysisService.Domain;

// FR-04, UC-01, UC-02: 検索結果を番号付き出典（元文書リンク付き）へ写像する純粋ロジック。
// 回答本文の [1][2] と出典番号を一致させるため、LLM へ渡す文脈と出典は同一の採番を使う。
public static class CitationMapper
{
    private const int SnippetMaxLength = 240;

    // 検索結果（チャンク単位）を 1 始まりの番号付き出典へ変換する。
    public static List<CitationDto> ToCitations(IReadOnlyList<SearchResultDto> results)
    {
        var citations = new List<CitationDto>(results.Count);
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            citations.Add(new CitationDto(
                Number: i + 1,
                DocumentId: r.DocumentId,
                DocumentTitle: r.DocumentTitle,
                ChunkId: r.ChunkId,
                SourceUri: ResolveSourceUri(r),
                Score: r.Score,
                Snippet: BuildSnippet(r.Text),
                // FR-04, SC-01, #541: 出典ごとの機密区分。文書属性から読み、欠落・空・未知値は
                // 安全側（restricted）へ縮退する。越境判定（FR-11）と同じ規則を使い、
                // 「画面には公開と出ているのにゲートウェイは restricted 扱い」を起こさない。
                Confidentiality: ConfidentialityLevels.FromAttributes(r.Attributes)));
        }
        return citations;
    }

    // LLM へ渡す参照文書文脈。出典番号と一致させる。
    public static string BuildContext(IReadOnlyList<CitationDto> citations)
    {
        var sb = new StringBuilder();
        foreach (var c in citations)
            sb.AppendLine($"[{c.Number}] {c.DocumentTitle}\n{c.Snippet}\n");
        return sb.ToString();
    }

    // 元文書へのリンク。正規化済み Markdown の URI を優先し、無ければ文書詳細への参照パスを返す。
    private static string ResolveSourceUri(SearchResultDto r)
        => !string.IsNullOrWhiteSpace(r.MarkdownUri)
            ? r.MarkdownUri!
            : $"/documents/{r.DocumentId}";

    private static string BuildSnippet(string text)
    {
        var normalized = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return normalized.Length <= SnippetMaxLength
            ? normalized
            : normalized[..SnippetMaxLength].TrimEnd() + "…";
    }
}
