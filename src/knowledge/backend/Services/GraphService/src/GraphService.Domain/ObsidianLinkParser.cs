using System.Text.RegularExpressions;

namespace GraphService.Domain;

// FR-17, ADR-0033 決定 8, IADR-0281 (#912): 正規化 Markdown 本文からリンクを抽出する**純粋関数**。
//
// **DB もイベントも見ない。** 入力は本文の文字列だけで、出力は「何と書いてあったか」の列である
// （型解決は EdgeTypeResolver、文書 ID への解決は Api 側の LinkEdgeSynchronizer が行う）。
// IADR-0280 決定 2・3 により Domain へ置く —— 外部ライブラリを参照せず、単体で試験できる。
//
// **本システムの正規化 Markdown は pandoc の変換物であり、Obsidian 記法が含まれる保証はない**
// （ADR-0033 決定 8 の具体化）。標準 Markdown リンクの解決が実務上の主戦場であり、
// Obsidian 記法は FR-20（Obsidian 連携）経由で入る個人資料のための規則である。
public static class ObsidianLinkParser
{
    // `!` の有無で埋め込みを区別する。内部に `[` `]` と改行を含まないものだけを 1 本のリンクとみなす。
    private static readonly Regex WikiLink = new(
        @"(!)?\[\[([^\[\]\r\n]+)\]\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 標準 Markdown リンク。`!` 付き（画像）は呼び出し側で落とす。
    private static readonly Regex MarkdownLink = new(
        @"(!)?\[([^\[\]\r\n]*)\]\(([^()\r\n]*)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // フロントマターの `key: value` 行。キーは英数・アンダースコア・ハイフン・ドットのみとする
    // （辺の型名の実体である以上、任意の文字列をキーとして拾う理由が無い）。
    private static readonly Regex FrontMatterKey = new(
        @"^(?<key>[A-Za-z_][A-Za-z0-9_.\-]*)\s*:\s*(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // フロントマターのリスト項目（`- 値`）。直前のキーに属する。
    private static readonly Regex FrontMatterItem = new(
        @"^\s*-\s*(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 本文からリンクを抽出する。**出現順で返す**（フロントマター → 本文）。
    public static IReadOnlyList<ObsidianLink> Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var text = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var (frontMatter, body) = SplitFrontMatter(text);

        var links = new List<ObsidianLink>();
        if (frontMatter is not null)
            ParseFrontMatter(frontMatter, links);
        ParseBody(body, links);
        return links;
    }

    // 先頭の `---` 〜 `---`（または `...`）をフロントマターとして切り出す。
    // **先頭行が `---` でなければフロントマターは無い**（本文中の水平線を誤って拾わない）。
    private static (string? FrontMatter, string Body) SplitFrontMatter(string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            return (null, text);

        var lines = text.Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line is "---" or "...")
                return (string.Join('\n', lines[1..i]), string.Join('\n', lines[(i + 1)..]));
        }

        // 閉じが無い＝フロントマターとして成立していない。全体を本文として扱う。
        return (null, text);
    }

    // フロントマターの値に現れる `[[...]]` を**キー名を型名として**拾う（ADR-0033 決定 8:
    // 「フロントマターでの明示指定は既定型より優先する。**書き手の明示が最も強い**」）。
    //
    // **キー名が辞書に無くてもここでは落とさない。** 未定義型の扱い（related へ丸めて警告）は
    // ADR-0033 決定 3 が定めており、その判断は EdgeTypeResolver の側にある。
    private static void ParseFrontMatter(string frontMatter, List<ObsidianLink> links)
    {
        string? key = null;
        foreach (var raw in frontMatter.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
                continue;

            string values;
            var keyMatch = FrontMatterKey.Match(line);
            if (keyMatch.Success)
            {
                key = keyMatch.Groups["key"].Value;
                values = keyMatch.Groups["rest"].Value;
            }
            else
            {
                var itemMatch = FrontMatterItem.Match(line);
                if (!itemMatch.Success || key is null)
                    continue;
                values = itemMatch.Groups["rest"].Value;
            }

            foreach (Match m in WikiLink.Matches(values))
            {
                var (target, anchor) = SplitTargetAndAnchor(m.Groups[2].Value);
                if (target.Length == 0)
                    continue;
                links.Add(new ObsidianLink(target, anchor, key, ObsidianLinkKind.Explicit));
            }
        }
    }

    private static void ParseBody(string body, List<ObsidianLink> links)
    {
        // フェンス（``` / ~~~）の中は本文ではない。**空白で潰して長さを保つ** ——
        // 出現順の突き合わせに位置を使うため、削除すると順序が入れ替わる。
        var text = BlankFencedBlocks(body);

        var found = new List<(int Index, ObsidianLink Link)>();
        var masked = text.ToCharArray();

        foreach (Match m in WikiLink.Matches(text))
        {
            Blank(masked, m.Index, m.Index + m.Length);
            var (target, anchor) = SplitTargetAndAnchor(m.Groups[2].Value);
            if (target.Length == 0)
                continue;

            var kind = m.Groups[1].Value == "!"
                ? ObsidianLinkKind.Embed
                : anchor is not null
                    ? ObsidianLinkKind.SectionReference
                    : ObsidianLinkKind.Reference;
            found.Add((m.Index, new ObsidianLink(target, anchor, null, kind)));
        }

        // Obsidian 記法を潰した後で標準 Markdown リンクを拾う（`![[a]]` を `[...](...)` として
        // 二重に数えない）。
        foreach (Match m in MarkdownLink.Matches(new string(masked)))
        {
            if (m.Groups[1].Value == "!")
                continue; // 画像は辺にしない。
            var target = NormalizeMarkdownTarget(m.Groups[3].Value);
            if (target is null)
                continue;
            found.Add((m.Index, new ObsidianLink(target, null, null, ObsidianLinkKind.MarkdownLink)));
        }

        links.AddRange(found.OrderBy(f => f.Index).Select(f => f.Link));
    }

    // `note`／`note#見出し`／`note|別名`／`folder/note.md#見出し` を (名前, アンカー) へ分ける。
    // **パスは最終セグメントで解決する**（IADR-0281。相対パスの起点は本文の置き場に依存し、
    // 正規化 Markdown の側にその情報が無い）。
    private static (string Target, string? Anchor) SplitTargetAndAnchor(string inner)
    {
        var value = inner;

        // 別名（`|`）は表示のためのものであり、リンク先の識別には使わない。
        var pipe = value.IndexOf('|');
        if (pipe >= 0)
            value = value[..pipe];

        string? anchor = null;
        var hash = value.IndexOf('#');
        if (hash >= 0)
        {
            anchor = value[(hash + 1)..].Trim();
            value = value[..hash];
            if (anchor.Length == 0)
                anchor = null;
        }

        return (NormalizeName(value), anchor);
    }

    // 最終セグメントを取り、`.md` を落として前後の空白を除く。
    private static string NormalizeName(string value)
    {
        var name = value.Trim();
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
            name = name[(slash + 1)..];
        if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            name = name[..^3];
        return name.Trim();
    }

    // 標準 Markdown リンクの宛先を名前へ正規化する。**辺にしないものは null を返す。**
    private static string? NormalizeMarkdownTarget(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0)
            return null;

        // `[t](<uri>)` の山括弧、`[t](uri "title")` のタイトルを落とす。
        if (value.StartsWith('<') && value.EndsWith('>'))
            value = value[1..^1].Trim();
        var space = value.IndexOf(' ');
        if (space > 0)
            value = value[..space];

        // 文書内アンカーだけの参照（`[t](#見出し)`）は他文書を指さない。
        if (value.StartsWith('#'))
            return null;

        // **外部 URL は辺にしない**（IADR-0281）。グラフのノードは本システムの文書であり、
        // 外部 URL に対応する文書が無い。スキーム付き絶対 URI をここで落とす。
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
            return null;

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            // 壊れたパーセントエンコードは復号せずそのまま扱う（抽出を止める理由にはならない）。
            decoded = value;
        }

        var hash = decoded.IndexOf('#');
        if (hash >= 0)
            decoded = decoded[..hash];

        var name = NormalizeName(decoded);
        return name.Length == 0 ? null : name;
    }

    // フェンス（``` / ~~~）で囲まれた領域を空白へ置き換える（長さは変えない）。
    private static string BlankFencedBlocks(string text)
    {
        var chars = text.ToCharArray();
        string? openMarker = null;
        var pos = 0;

        while (pos <= chars.Length)
        {
            var eol = text.IndexOf('\n', pos);
            var end = eol < 0 ? chars.Length : eol;
            var trimmed = text[pos..end].TrimStart();
            var marker = trimmed.StartsWith("```", StringComparison.Ordinal) ? "```"
                : trimmed.StartsWith("~~~", StringComparison.Ordinal) ? "~~~"
                : null;

            if (openMarker is null)
            {
                if (marker is not null)
                {
                    openMarker = marker;
                    Blank(chars, pos, end);
                }
            }
            else
            {
                Blank(chars, pos, end);
                if (marker == openMarker)
                    openMarker = null;
            }

            if (eol < 0)
                break;
            pos = eol + 1;
        }

        return new string(chars);
    }

    private static void Blank(char[] chars, int start, int end)
    {
        for (var i = start; i < end && i < chars.Length; i++)
            chars[i] = ' ';
    }
}
