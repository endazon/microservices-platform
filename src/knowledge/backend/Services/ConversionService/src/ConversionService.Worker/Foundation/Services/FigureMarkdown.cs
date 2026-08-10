namespace ConversionService.Worker.Foundation.Services;

// FR-12, UC-06, SC-07, IADR-0154 決定 3: 縮退した図の本文への埋め込み形と、その置換。
//
// **単一情報源にしてある。** 埋め込む側（NormalizationService）と置換する側（人手補正）が別々に
// 形を書くと、片方だけ変えたときに置換が静かに空振りする——**本文は変わらないのに補正は保存済み**
// という、壊れたと分かりにくい失敗になる。計画が「マージを採らない」理由（05_screens:334）と同じ
// 種類の危険であるため、形は 1 箇所に閉じる。
public static class FigureMarkdown
{
    // 画像保持へ縮退した図の埋め込み（`![figureId](uri)`）。
    public static string ImageEmbed(string figureId, string uri) => $"![{figureId}]({uri})";

    // 人手補正で入ったコード片の埋め込み（コードブロック）。
    public static string CodeEmbed(string language, string code) => $"```{language}\n{code}\n```";

    // 本文中の図の埋め込みを、補正後のコードブロックへ置き換える。
    // 置換できなかった場合は false を返す（呼び出し側は本文を保存しない）。
    // **見つからないまま保存すると、補正が保存済みなのに本文へ反映されない状態が残る。**
    public static bool TryReplaceImageWithCode(string markdown, string figureId, string imageUri,
        string language, string code, out string replaced)
    {
        var target = ImageEmbed(figureId, imageUri);
        var index = markdown.IndexOf(target, StringComparison.Ordinal);
        if (index < 0)
        {
            replaced = markdown;
            return false;
        }

        replaced = string.Concat(
            markdown.AsSpan(0, index),
            CodeEmbed(language, code),
            markdown.AsSpan(index + target.Length));
        return true;
    }
}
