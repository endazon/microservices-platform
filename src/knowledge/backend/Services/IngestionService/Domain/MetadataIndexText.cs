namespace IngestionService.Domain;

// FR-02, FR-03, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 2, #1253, [[IADR-0388]] 決定 4・5:
// **本文を持たない文書を検索に載せるための索引テキスト**を、メタデータから作る唯一の点。
//
// 材料は**題名・タグ・原本の所在（パス）・データソースの表示名**である。
//
// 🔴 **［2026-09-05 / #1253］パスとデータソース名を足した。** 従前は題名とタグだけで、
// ADR-0070 決定 4 の「タイトル・**パス**・**データソース**・更新日時など」のうち
// **題名でしか当たらなかった**（#1193 受け入れ基準 2 が半分しか満たされていなかった）。
// 届いていなかったのは契約の側で、`RawDocumentFetched.OriginalPath` は ConversionService が
// **拡張子なしファイル名 = 題名**へ畳んだ時点で終わっていた（[[IADR-0358]] フォローアップ 1）。
// `DocumentNormalized` / `DocumentUpdated` が末尾・既定値つきで運ぶようになったので、ここで使う。
//
// **更新日時はここに入れない** —— 点のペイロード `updated_at` が既に持っており（[[IADR-0149]]）、
// 並び順はそちらが引く。文字列にして全文検索へ混ぜると、日付らしき語で無関係に当たる。
//
// 🔴 **ABAC 属性の値は入れない。** 入れると「`confidential` で検索すると機密文書が並ぶ」という、
// 絞り込み（`AttributeFilters`）とは別経路の当て方が生まれる。属性は**絞る**ためのものである。
// この線は #1253 でも動かしていない（[[IADR-0358]] 決定 2）。
//
// 🔴 **本文ありのチャンクには載せない**（[[IADR-0388]] 決定 5 の非対称）。載せると全文側に
// パスの断片が当たり、「本文に書いてある語で当たった」と「置き場所の名前で当たった」が
// 抜粋から区別できなくなる。**本文なしの点は抜粋が空なので、その混同が起きない。**
public static class MetadataIndexText
{
    /// <summary>
    /// 題名・タグ・所在・データソース名を空白区切りで並べる。CJK の 2-gram 化
    /// （<c>CjkBigramPayload</c>）は書き込み側がこの文字列に対して行うので、ここでは行わない。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>パスは区切り文字を空白へ開いてから入れる。</b> <c>/共有/経理/2026年度.pdf</c> を
    /// そのまま入れると、全文側では 1 つの長い語として索引され「経理」で当たらない
    /// （CJK 2-gram は当たるが、英数字のフォルダ名は当たらない）。区切りを開くと
    /// フォルダ名・ファイル名がそれぞれ語になる。拡張子は落とす —— <c>pdf</c> で
    /// 全 PDF が並ぶのは絞り込みの役に立たない。
    /// </remarks>
    public static string Build(string? title, IEnumerable<string>? tags,
        string? originalPath = null, string? dataSourceName = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(title)) parts.Add(title.Trim());

        if (tags is not null)
        {
            foreach (var tag in tags)
                if (!string.IsNullOrWhiteSpace(tag)) parts.Add(tag.Trim());
        }

        foreach (var segment in PathSegments(originalPath)) parts.Add(segment);

        if (!string.IsNullOrWhiteSpace(dataSourceName)) parts.Add(dataSourceName.Trim());

        // **同じ語を 2 度並べない。** 題名は原本のファイル名（拡張子なし）なので、
        // パスの最終要素とほぼ必ず重なる。重複は検索の当たりを増やさず文字列を膨らませるだけである。
        return string.Join(' ', parts.Distinct(StringComparer.Ordinal));
    }

    /// <summary>所在を区切り文字で分け、拡張子を落とした語の列にする（空要素は捨てる）。</summary>
    private static IEnumerable<string> PathSegments(string? originalPath)
    {
        if (string.IsNullOrWhiteSpace(originalPath)) yield break;

        foreach (var raw in originalPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Trim();
            if (segment.Length == 0) continue;

            // 拡張子を落とす（途中のフォルダ名にドットが在っても壊さないよう、
            // 「最後のドットより後ろが 1〜8 文字の **ASCII** 英数字」のときだけ落とす）。
            // 🔴 **ASCII に限るのが要点である。** `char.IsLetterOrDigit` は CJK も真を返すので、
            // それで判定すると `v1.2.仕様` の「仕様」が拡張子と見なされて消える（実測で落とした）。
            var dot = segment.LastIndexOf('.');
            if (dot > 0 && dot < segment.Length - 1)
            {
                var ext = segment[(dot + 1)..];
                if (ext.Length <= 8 && ext.All(char.IsAsciiLetterOrDigit)) segment = segment[..dot];
            }

            if (segment.Length > 0) yield return segment;
        }
    }
}
