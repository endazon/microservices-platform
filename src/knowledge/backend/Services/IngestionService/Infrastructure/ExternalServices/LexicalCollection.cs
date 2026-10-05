using Microsoft.Extensions.Configuration;

namespace IngestionService.Infrastructure.ExternalServices;

// FR-02, FR-03, ADR-0127 決定 1, [[IADR-0497]] 決定 1 (#1746): **語彙索引のコレクション名。**
//
// ベクトルを持たない専用のコレクションである（Qdrant v1.18.1 で実測。作業仕様書 §実測）。
// 🔴 **取り込みと検索で同じ設定キー（`Qdrant:LexicalCollection`）を読む。** 片方だけ変えると
// 「書いたのに検索されない」が静かに起きる（#1215 と同型）。Helm は 1 つの値を両サービスへ描画する。
//
// 🔴 **無効化の口は持たない。** 空・未設定は既定名へ倒す（ADR-0127 は高機密文書を語彙索引に
// 載せると決めた。載せない構成を設定 1 つで作れると、以前の「どの検索でも見つからない」へ黙って戻る）。
public static class LexicalCollection
{
    public const string ConfigKey = "Qdrant:LexicalCollection";
    public const string DefaultName = "knowledge_chunks_lexical";

    public static string Resolve(IConfiguration configuration)
    {
        var name = configuration[ConfigKey]?.Trim();
        return string.IsNullOrEmpty(name) ? DefaultName : name;
    }

    // 🔴 ベクトルのコレクションと同名にしない（起動を止める）。同名にすると、語彙索引の点
    // （ベクトルなし）がモデル別コレクションへ混ざり、取り込みのブートストラップは既存の
    // ベクトルのコレクションを語彙索引と取り違える。
    public static void EnsureDistinct(string lexical, IEnumerable<string> vectorCollections)
    {
        if (vectorCollections.Contains(lexical, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"{ConfigKey} '{lexical}' は Embedding:Collections のベクトルのコレクションと同名である。"
                + " 語彙索引はベクトルを持たない専用のコレクションに置く（IADR-0497 決定 1）。別の名前を与えること。");
    }
}
