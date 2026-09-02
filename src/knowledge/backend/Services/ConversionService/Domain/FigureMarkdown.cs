namespace ConversionService.Domain;

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

    // FR-12, IADR-0351 決定 2 (#1120): 変換直後の本文が図を指す**暫定の目印**のスキーム。
    //
    // pandoc の `--extract-media` は本文中の画像参照を一時ディレクトリの絶対パスへ書き換えるが、
    // そのディレクトリは変換直後に消える。いっぽう最終参照（`storage://…/assets/fig-1.png`）は
    // **図をアップロードするまで判らない**。位置を知っているのは変換器、最終参照を知っているのは
    // 正規化オーケストレータであり、**両者を繋ぐ 2 段の目印**が要る。
    //
    // 目印の綴りは `ImageEmbed` そのもの（`![id](uri)`）にしてある —— 置換に失敗して本文へ残っても
    // 「どの図の目印か」が読め、`TryReplacePlaceholder` の走査対象としても 1 箇所で決まる。
    public const string PlaceholderScheme = "figure:";

    // 図 1 つを指す暫定参照（`figure:fig-1`）。
    public static string PlaceholderUri(string figureId) => PlaceholderScheme + figureId;

    // 図 1 つの暫定の埋め込み（`![fig-1](figure:fig-1)`）。
    public static string PlaceholderEmbed(string figureId) =>
        ImageEmbed(figureId, PlaceholderUri(figureId));

    // 本文中の暫定の目印を、最終的な埋め込み（画像参照またはコードブロック）へ置き換える。
    // 目印が 1 つも無ければ false を返す（呼び出し側は末尾へ append する。IADR-0351 決定 6）。
    //
    // **全出現を置換する。** 同じ媒体を 2 度参照する原本では目印も 2 つ置かれ、片方だけ置換すると
    // 解決できない `figure:` 参照が本文へ残る —— 本 ADR が直している事故がまさにそれである
    // （人手補正の `TryReplaceImageWithCode` が先頭 1 件のみなのは IADR-0154 決定 3 の別判断）。
    public static bool TryReplacePlaceholder(string markdown, string figureId, string embed,
        out string replaced)
    {
        var target = PlaceholderEmbed(figureId);
        if (markdown.IndexOf(target, StringComparison.Ordinal) < 0)
        {
            replaced = markdown;
            return false;
        }

        replaced = markdown.Replace(target, embed, StringComparison.Ordinal);
        return true;
    }

    // 人手補正で入ったコード片の埋め込み（コードブロック）。
    public static string CodeEmbed(string language, string code) => $"```{language}\n{code}\n```";

    // UC-06, IADR-0154 決定 3: コードフェンスを内側から閉じられる入力を弾く。
    //
    // **`code` に ``` が含まれるとフェンスがそこで閉じ、残りが生の本文として解釈される。**
    // この本文は保存されて `DocumentNormalized` で再発行され、**補正操作をしていない一般利用者が
    // 読む文書**に永続化される。ログの無害化（`ForLog`）と同じ種類の危険が、より影響の大きい
    // 経路に残っていた（PR #650 レビュー 2 巡目の指摘）。
    //
    // **エスケープではなく拒否にする。** Phase 1 が受け取るのは PlantUML / Mermaid のコード片であり、
    // ``` を含むことは正常系に無い。`~~~` へ逃がす・内容を書き換えるといった小細工は、
    // **利用者が投稿したものと保存されたものがずれる**——「見つからなければ保存しない」
    // （`TryReplaceImageWithCode`）と同じ思想で、曖昧なら受け取らない。
    public static bool IsEmbeddable(string? language, string? code) =>
        !string.IsNullOrWhiteSpace(language)
        && !string.IsNullOrWhiteSpace(code)
        && !language.Contains('`')
        && !language.Contains('\n')
        && !code.Contains("```", StringComparison.Ordinal);

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
