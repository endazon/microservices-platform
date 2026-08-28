using IngestionService.Worker.Domain.Ports;
namespace IngestionService.Worker.Domain;

// FR-02: Markdown の見出しで分割し、最大トークン数を超えたら文単位で分割する。
// 長いセクションを分割する際は overlap 分だけ直前チャンク末尾を引き継ぎ、
// 文脈の断絶を防ぐ（4文字 ≒ 1トークンの簡易推定）。
public class MarkdownChunkingService : IChunkingService
{
    private const int CharsPerToken = 4;

    public List<string> Chunk(string markdownText, int maxTokens = 512, int overlap = 50)
    {
        if (string.IsNullOrWhiteSpace(markdownText)) return [];

        var maxChars = maxTokens * CharsPerToken;
        var overlapChars = Math.Max(0, overlap) * CharsPerToken;

        // 見出し行（#〜######）で分割
        var sections = System.Text.RegularExpressions.Regex
            .Split(markdownText, @"(?m)^#{1,6}\s")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var chunks = new List<string>();
        foreach (var section in sections)
        {
            var trimmed = section.Trim();
            if (trimmed.Length <= maxChars)
            {
                chunks.Add(trimmed);
                continue;
            }

            // 長いセクションは文単位で詰め、maxChars 到達で切り出す。
            var sentences = trimmed.Split(new[] { '。', '.', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = new System.Text.StringBuilder();
            foreach (var sentence in sentences)
            {
                if (current.Length + sentence.Length > maxChars && current.Length > 0)
                {
                    chunks.Add(current.ToString().Trim());
                    // FR-02: overlap 分だけ直前チャンク末尾を次チャンク先頭へ引き継ぐ
                    var carry = TakeTail(current.ToString().Trim(), overlapChars);
                    current.Clear();
                    if (carry.Length > 0) current.Append(carry).Append(' ');
                }
                current.Append(sentence).Append(' ');
            }
            if (current.Length > 0) chunks.Add(current.ToString().Trim());
        }

        return chunks.Where(c => c.Length > 0).ToList();
    }

    // 末尾から overlapChars 文字を、できるだけ単語/文の境界で切り出す
    private static string TakeTail(string text, int overlapChars)
    {
        if (overlapChars <= 0) return string.Empty;
        if (text.Length <= overlapChars) return text;
        var tail = text[^overlapChars..];
        var space = tail.IndexOf(' ');
        return space >= 0 && space < tail.Length - 1 ? tail[(space + 1)..] : tail;
    }
}
