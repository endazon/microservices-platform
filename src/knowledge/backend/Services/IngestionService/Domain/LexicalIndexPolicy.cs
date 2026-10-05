using Knowledge.Contracts.Dtos;

namespace IngestionService.Domain;

// FR-02, FR-05, ADR-0127 決定 1・4, [[IADR-0497]] 決定 2 (#1746): **埋め込まず語彙索引にだけ載せる文書の判定。**
//
// 計画 ADR-0127 は `confidential`・`restricted` と、機密区分が未指定・未知の文書を
// 「埋め込みを作らず、全文索引（語彙索引）にだけ載せる」と決めた。本文はどの埋め込みの送信先
// （ティア A・B を問わず）へも送らない。
//
// 🔴 **埋め込みを呼ぶ前に、ここで分ける。** ゲートウェイの拒否（`Embedded=false`）を見てから
// 語彙索引へ回す形は採らない —— それは「送ってみて断られたら」であり、ティア A（Ruri）を opt-in で
// 有効にした配備では**断られずに埋め込まれてしまう**（ADR-0127 は「埋め込まない」であって
// 「ティア A が無ければ埋め込まない」ではない）。加えて、本文がゲートウェイへ 1 度渡る。
//
// 🔴 **許す側を列挙する（allow-list）。** 埋め込んでよいのは `public` / `internal` だけで、それ以外は
// 未指定・空・未知の値を含めてすべて語彙索引へ倒す。正規化は知識ユニット共通の
// `ConfidentialityLevels.FromAttributes`（未知・未指定は `restricted`）を通す —— ゲートウェイの
// `SensitivityClasses.Parse`（前後の空白を落とす）より厳しく、食い違うときは**埋め込まない側**へ倒れる
// （`" public "` はここでは `restricted` になる）。逆向き（ここで埋めるのにゲートウェイが拒む）は起きない。
public static class LexicalIndexPolicy
{
    // 埋め込んでよい機密区分（これ以外はすべて語彙索引だけ）。
    private static readonly string[] EmbeddableLevels =
        [ConfidentialityLevels.Public, ConfidentialityLevels.Internal];

    public static bool IsLexicalOnly(IReadOnlyDictionary<string, string> attributes)
        => !EmbeddableLevels.Contains(ConfidentialityLevels.FromAttributes(attributes), StringComparer.Ordinal);
}
