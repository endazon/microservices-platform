using System.Globalization;

namespace WikiService.Domain;

// 🔴 FR-13, [[IADR-0554]] 決定 1・2 (#1966): **文字列を DB の varchar(n) に収める切り詰め。**
//
// - **数える単位は符号位置（Rune）**。PostgreSQL の varchar(n) は文字で数え、UTF-8 の DB では 1 文字＝1 符号位置である
//   （バイト数でも、C# の string.Length＝UTF-16 の単位数でもない。BMP 外の文字は Length では 2 と数えてしまう）。
// - **切る位置は書記素クラスタ（テキスト要素）の境界に限る**。サロゲートペア・結合文字（濁点の U+3099 等）・異体字セレクタ・
//   ZWJ で連結した絵文字を途中で切らない（切ると表示が化け、孤立したサロゲートは DB へ入らない）。
// - 切ったら末尾に印（`…`・1 文字）を付け、**印を含めて上限に収める**。上限以内の文字列は同じインスタンスをそのまま返す。
public static class WikiText
{
    public const string Ellipsis = "…";

    // 符号位置の数（PostgreSQL の varchar の数え方）。孤立したサロゲートは 1 と数える（置換文字として読まれる）。
    public static int CountChars(string value)
    {
        var n = 0;
        foreach (var _ in value.EnumerateRunes()) n++;
        return n;
    }

    // value を maxChars 文字（符号位置）以内に収める。切ったときだけ truncated=true。
    // mark=false は印を付けない（slug のように、印が値の意味を変える列のため）。
    public static string FitColumn(string value, int maxChars, out bool truncated, bool mark = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        if (CountChars(value) <= maxChars)
        {
            truncated = false;
            return value;
        }

        truncated = true;
        var budget = mark ? maxChars - CountChars(Ellipsis) : maxChars;
        var end = 0;
        var used = 0;
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var size = CountChars(element);
            if (used + size > budget) break;
            used += size;
            end = elements.ElementIndex + element.Length;
        }

        var head = value[..end].TrimEnd();
        return mark ? head + Ellipsis : head;
    }

    public static string FitColumn(string value, int maxChars, bool mark = true)
        => FitColumn(value, maxChars, out _, mark);
}
