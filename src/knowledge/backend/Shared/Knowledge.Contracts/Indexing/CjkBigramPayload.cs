using System.Text;

namespace Knowledge.Contracts.Indexing;

// FR-03, UC-01, ADR-0009, #1118, [[IADR-0339]] 決定 1:
// **日本語（CJK）の語で全文検索が当たるための、取り込み側と検索側の共通契約。**
//
// Qdrant 公式イメージ v1.18.1 の `multilingual` トークナイザは**日本語の分かち書きを持たず**、語で当たるかは
// CJK の連なりの切れ目次第である（稼働 Qdrant で実測: 実配備チャンクに実在する日本語 25 語のうち当たるのは 1 語、
// 実在する 2-gram 176 種のうち当たるのは 1 つ）。形態素解析器を積んだ自前ビルドは計画の裁定を要する。
//
// そこで**アプリ側で CJK の連なりを 2-gram に割り、別のペイロード `text_ngram` として Qdrant 自身の
// 全文索引に載せる**。Qdrant の全文 Match は**全トークンの存在**を要求するので、クエリを同じ変換に
// かければ「その並びの 2-gram をすべて含む」＝部分文字列一致に近い意味論になる（同じ実測で 25/25 語・
// 176/176 の 2-gram が当たり、在らない 5 語は 0 件）。
//
// 🔴 **取り込み側（書く）と検索側（読む）が同じ 1 つの変換を使わなければならない。**
// ペイロードのキー文字列（`document_id` / `text`）は両サービスへ複写して揃えてきたが（[[IADR-0014]]）、
// **変換の関数を複写すると必ず割れる**（片方だけ直すと静かに 0 件へ落ちる）ので、ここ（契約）に 1 つだけ置く。
//
// 索引側の宣言（tokenizer=prefix / min_token_len=1 / max_token_len=2）は取り込みサービス
// `QdrantIngestionVectorStore.BuildCjkNgramIndexParams()` が持つ。`prefix` を選ぶのは、
// 2-gram の 1 文字接頭辞も索引に入るため **1 文字の語（「本」）も当たる**からである（実測）。
public static class CjkBigramPayload
{
    // 取り込み側の書き込み・索引と、検索側の Match が同じ 1 つの値を使う。
    public const string PayloadKey = "text_ngram";

    // 2-gram の区切り。索引側は空白で割る（`prefix` トークナイザも空白・記号で語を切る）。
    private const char Separator = ' ';

    /// <summary>
    /// CJK の連なりごとに 2-gram（1 文字の連なりは 1-gram）を空白区切りで並べる。
    /// CJK 以外の文字は区切りとしてだけ働き、出力には含まれない（そちらは `text` の索引が引く）。
    /// CJK を含まなければ空文字列。
    /// </summary>
    public static string Encode(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder();
        var run = new List<Rune>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsCjk(rune))
            {
                run.Add(rune);
                continue;
            }

            AppendGrams(sb, run);
            run.Clear();
        }

        AppendGrams(sb, run);
        return sb.ToString();
    }

    /// <summary>
    /// クエリを「CJK 以外（`text` へ Match する）」と「CJK の 2-gram（`text_ngram` へ Match する）」に割る。
    /// どちらかが空なら、その系統の条件は出さない（呼び出し側の責務）。
    /// </summary>
    public static (string NonCjk, string Ngram) SplitQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (string.Empty, string.Empty);

        var nonCjk = new StringBuilder();
        var pendingSpace = false;
        foreach (var rune in query.EnumerateRunes())
        {
            if (IsCjk(rune) || Rune.IsWhiteSpace(rune))
            {
                pendingSpace = nonCjk.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                nonCjk.Append(Separator);
                pendingSpace = false;
            }

            nonCjk.Append(rune.ToString());
        }

        return (nonCjk.ToString(), Encode(query));
    }

    // CJK と見なす範囲。漢字（統合漢字・拡張 A・互換漢字・拡張 B 以降）、ひらがな、カタカナ
    // （音標拡張・半角を含む）、および `々〆〤`（CJK 記号のうち語の一部になるもの。`ー` はカタカナ範囲に在る）。
    // 全角英数（U+FF10〜）は含めない —— それは識別子の系統（`text` / multilingual）が引く。
    public static bool IsCjk(Rune rune)
    {
        var v = rune.Value;
        return v is >= 0x3040 and <= 0x309F      // ひらがな
            or >= 0x30A0 and <= 0x30FF           // カタカナ（`ー` U+30FC を含む）
            or >= 0x31F0 and <= 0x31FF           // カタカナ音標拡張
            or >= 0xFF66 and <= 0xFF9F           // 半角カタカナ
            or >= 0x3400 and <= 0x4DBF           // 漢字 拡張 A
            or >= 0x4E00 and <= 0x9FFF           // 統合漢字
            or >= 0xF900 and <= 0xFAFF           // 互換漢字
            or >= 0x20000 and <= 0x2FA1F         // 拡張 B〜
            or 0x3005 or 0x3006 or 0x3024;       // 々 〆 〤
    }

    private static void AppendGrams(StringBuilder sb, List<Rune> run)
    {
        if (run.Count == 0)
            return;

        if (run.Count == 1)
        {
            Append(sb, run[0].ToString());
            return;
        }

        for (var i = 0; i + 1 < run.Count; i++)
            Append(sb, run[i].ToString() + run[i + 1].ToString());
    }

    private static void Append(StringBuilder sb, string gram)
    {
        if (sb.Length > 0)
            sb.Append(Separator);
        sb.Append(gram);
    }
}
