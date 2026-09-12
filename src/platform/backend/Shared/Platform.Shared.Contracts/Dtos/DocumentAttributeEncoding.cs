namespace Platform.Shared.Contracts.Dtos;

// FR-05, FR-19, ADR-0036 D-06, ADR-0080 決定 2, ADR-0098 フォローアップ 2, #1448:
// **文書側の集合値属性**（`shared_with` / `tags`）を 1 キー 1 値の契約（`Dictionary<string, string>`）へ
// 載せるときの符号化と、認可フィルタとの突合の**唯一の述語**。
//
// ■ 🔴 なぜ契約プロジェクトに置くのか
//   `AttributeFilter` を文書属性と突き合わせる面は 3 つあった —— BFF（`BffScopeResolver.MatchesAll`）・
//   GraphService（`AbacNodeFilter`）・WikiService（`AbacPageFilter`）。いずれも**値を単一文字列として**
//   `AllowedValues.Contains(value)` で比べており、集合値の `shared_with`（文書ごとに可変長の共有先）は
//   **1 件も一致しなかった**（#1448）。検索側（RetrievalService。Qdrant のリスト項目 `Match.Keywords`）だけが
//   交差で判定しており、**同じスコープが面によって違う答えを出していた**。3 面が互いを参照できない
//   （`src/README.md` の依存規則）ため、述語は契約の側に 1 つだけ持つ。
//
// ■ 線上表現は `UserAttributeEncoding` と同じカンマ連結である（IADR-0385 決定 2）
//   **表現を 2 つ持たない** —— 索引は配列（Qdrant の `ListValue`）、1 キー 1 値の契約はカンマ連結、という
//   2 層は利用者属性 `tags` が既に採っている形であり、分割規則も `UserAttributeEncoding.Split` を再利用する。
//
// ■ 🔴 集合として読むのは `shared_with` と `tags` だけである
//   `confidentiality` / `department` / `doc_scope` / `owner` は単一値であり、ここへ足してはならない。
//   一律に分割すると、値にカンマを含む単一値属性が別の意味に化ける（deny 側だが**静かに壊れる**）。
public static class DocumentAttributeEncoding
{
    /// <summary>共有先（共有台帳の `subjectId` の集合）。索引のリスト項目 `shared_with` と同じ綴り。</summary>
    public const string SharedWithKey = "shared_with";

    /// <summary>文書のタグ（索引のリスト項目 `tags` と同じ綴り）。</summary>
    public const string TagsKey = "tags";

    /// <summary>集合として読む文書属性キー。**ここに無いキーは 1 値である。**</summary>
    public static IReadOnlySet<string> SetValuedKeys { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SharedWithKey, TagsKey };

    public static bool IsSetValued(string key) => SetValuedKeys.Contains(key);

    /// <summary>
    /// 文書の属性辞書に共有先の集合を `shared_with` として重ねた**読み取り用の像**を返す。
    /// 共有先が空なら `shared_with` を載せない（**空集合は載せない** —— 載せると「空文字と一致」の余地が生まれる）。
    /// **元の辞書は変更しない**（`DocumentDto.Attributes` は書き戻しの入力にもなる。共有先を属性へ混ぜて保存させない）。
    /// </summary>
    public static IReadOnlyDictionary<string, string> WithSharedWith(
        IReadOnlyDictionary<string, string> attributes, IEnumerable<string>? sharedWith)
    {
        var joined = UserAttributeEncoding.Join(sharedWith ?? []);
        if (joined.Length == 0) return attributes;

        var view = new Dictionary<string, string>(attributes, StringComparer.Ordinal)
        {
            [SharedWithKey] = joined,
        };
        return view;
    }
}

// FR-05, FR-19, ADR-0080 決定 2, #1448: 認可フィルタ（`AttributeFilter`）と文書属性の突合。
//
// **フィルタ間 AND・値集合内 OR。属性キーを持たない文書は不一致（欠落は安全側に倒す）。**
// 🔴 **集合値キーは交差（`∩ ≠ ∅`）で判定する** —— 部分集合ではない（ADR-0080 決定 2。利用者側の `tags` と同じ）。
// 単一値キーは値一致（大小文字無視）で、**従前の判定を 1 文字も変えない**。
public static class AttributeFilterMatch
{
    public static bool MatchesAll(
        IReadOnlyDictionary<string, string> attributes, IReadOnlyList<AttributeFilter> filters)
    {
        foreach (var filter in filters)
        {
            if (!attributes.TryGetValue(filter.Key, out var value))
                return false;
            if (!MatchesOne(filter, value))
                return false;
        }
        return true;
    }

    /// <summary>1 フィルタと 1 属性値の突合。集合値キーは交差、単一値キーは値一致。</summary>
    public static bool MatchesOne(AttributeFilter filter, string value)
    {
        if (DocumentAttributeEncoding.IsSetValued(filter.Key))
        {
            var members = UserAttributeEncoding.Split(value);
            // 空集合（空文字・区切りだけ）は何にも一致しない。
            return members.Count > 0 && members.Overlaps(filter.AllowedValues);
        }
        return filter.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase);
    }
}
