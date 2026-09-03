namespace IngestionService.Domain;

// FR-02, FR-03, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 2:
// **本文を持たない文書を検索に載せるための索引テキスト**を、メタデータから作る唯一の点。
//
// 🔴 **ADR-0070 決定 4 は「タイトル・パス・データソース・更新日時など」と書くが、
// 取り込みの口（`DocumentUpdated`）に届いているのは題名・タグ・更新日時・属性だけである**（実測）。
// 取り込み元のパス（`RawDocumentFetched.OriginalPath`）は ConversionService が
// **拡張子なしファイル名 = 題名**へ畳んだ時点で終わっており、`DocumentNormalized` も
// `DocumentUpdated` もパスを運ばない。**題名は原本のファイル名なので決定 4 の「タイトル」は満たす。**
// パスとデータソース名を載せるにはイベント契約の変更が要る（[[IADR-0358]] フォローアップ 1）。
//
// **更新日時はここに入れない** —— 点のペイロード `updated_at` が既に持っており（[[IADR-0149]]）、
// 並び順はそちらが引く。文字列にして全文検索へ混ぜると、日付らしき語で無関係に当たる。
//
// 🔴 **ABAC 属性の値は入れない。** 入れると「`confidential` で検索すると機密文書が並ぶ」という、
// 絞り込み（`AttributeFilters`）とは別経路の当て方が生まれる。属性は**絞る**ためのものである。
public static class MetadataIndexText
{
    /// <summary>
    /// 題名とタグを空白区切りで並べる。CJK の 2-gram 化（`CjkBigramPayload`）は書き込み側が
    /// この文字列に対して行うので、ここでは行わない。
    /// </summary>
    public static string Build(string? title, IEnumerable<string>? tags)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(title)) parts.Add(title.Trim());

        if (tags is not null)
        {
            foreach (var tag in tags)
                if (!string.IsNullOrWhiteSpace(tag)) parts.Add(tag.Trim());
        }

        return string.Join(' ', parts);
    }
}
