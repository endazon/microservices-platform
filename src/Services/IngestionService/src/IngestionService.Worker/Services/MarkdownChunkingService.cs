namespace IngestionService.Worker.Services;

// FR-02: Markdown の見出しで分割し、最大トークン数を超えたら文単位で分割
public class MarkdownChunkingService : IChunkingService
{
    public List<string> Chunk(string markdownText, int maxTokens = 512, int overlap = 50)
    {
        if (string.IsNullOrWhiteSpace(markdownText)) return [];

        // 見出し行（#〜######）で分割
        var sections = System.Text.RegularExpressions.Regex
            .Split(markdownText, @"(?m)^#{1,6}\s")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var chunks = new List<string>();
        foreach (var section in sections)
        {
            // 簡易トークン推定（4文字≒1トークン）
            if (section.Length / 4 <= maxTokens)
            {
                chunks.Add(section.Trim());
                continue;
            }

            // 長いセクションは文単位で分割
            var sentences = section.Split(new[] { '。', '.', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = new System.Text.StringBuilder();
            foreach (var sentence in sentences)
            {
                if ((current.Length + sentence.Length) / 4 > maxTokens && current.Length > 0)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                }
                current.Append(sentence).Append(' ');
            }
            if (current.Length > 0) chunks.Add(current.ToString().Trim());
        }

        return chunks.Where(c => c.Length > 0).ToList();
    }
}
