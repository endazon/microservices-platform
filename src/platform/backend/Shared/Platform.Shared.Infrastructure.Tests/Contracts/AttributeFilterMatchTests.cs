using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;

namespace Platform.Shared.Infrastructure.Tests.Contracts;

// FR-05, FR-19, 計画 ADR-0036 D-06, ADR-0080 決定 2, ADR-0098 決定 1, [[IADR-0385]] 決定 2,
// [[IADR-0448]] (#1448): 認可フィルタと**文書側**属性の突合を固定する。
//
// 🔴 **この述語が唯一の所在である。** 従前、同じ論理が 3 面（BFF の `BffScopeResolver`・
// GraphService の `AbacNodeFilter`・WikiService の `AbacPageFilter`）に複製され、いずれも値を
// **単一文字列**として `AllowedValues.Contains(value)` で比べていた —— 集合値の `shared_with`
// （文書ごとに可変長の共有先）は**1 件も一致しなかった**（#1448）。検索側（Qdrant のリスト項目
// `Match.Keywords`）だけが交差で判定しており、**同じスコープが面によって違う答えを出していた。**
// 3 面は互いを参照できない（`src/README.md` の依存規則）ので、規則を各側へ写すと食い違いを
// そのまま再生産する。
//
// 🔴 **陽性対照（単一値の判定が 1 文字も変わっていない）を対で置く。** 集合値の話だけを測ると、
// 「一律に分割する」実装でも緑になる —— それは `confidentiality` にカンマを含む値が別の意味へ
// 化ける壊れ方であり、`ADR-0080` と [[IADR-0385]] が明示的に禁じた形である。
[Trait("TestKind", "Unit")]
public class AttributeFilterMatchTests
{
    // 集合値として読むのは 2 キーだけである。**単一値キーを足すと静かに壊れる。**
    [Theory]
    [InlineData("shared_with", true)]
    [InlineData("tags", true)]
    [InlineData("SHARED_WITH", true)]
    // 陰性対照（対で置く）: これらは 1 値であり、区切り文字を見出してはならない。
    [InlineData("confidentiality", false)]
    [InlineData("department", false)]
    [InlineData("doc_scope", false)]
    [InlineData("owner", false)]
    public void Only_the_declared_keys_are_read_as_sets(string key, bool expected)
        => DocumentAttributeEncoding.IsSetValued(key).Should().Be(expected);

    // ── 単一値キー: 従前の判定（値一致・大小文字無視）が 1 文字も変わらない ──────────

    [Theory]
    [InlineData("internal", true)]
    [InlineData("INTERNAL", true)]
    [InlineData("public", true)]
    [InlineData("confidential", false)]
    public void A_single_valued_key_matches_by_value_ignoring_case(string value, bool expected)
        => AttributeFilterMatch.MatchesOne(
                new AttributeFilter("confidentiality", ["internal", "public"]), value)
            .Should().Be(expected);

    // 🔴 単一値キーは**分割されない**。`"internal,public"` は「internal でも public でもない値」である
    // （辞書外の値が「要素」として通るのを禁じた [[IADR-0385]] の禁則）。
    [Fact]
    public void A_single_valued_key_is_never_split()
        => AttributeFilterMatch.MatchesOne(
                new AttributeFilter("confidentiality", ["internal"]), "internal,public")
            .Should().BeFalse("一律に分割すると、値にカンマを含む単一値属性が別の意味に化ける");

    // ── 集合値キー: 交差（`∩ ≠ ∅`）で判定する ────────────────────────────

    [Theory]
    [InlineData("a,b", true)]          // 交差 {b}
    [InlineData("b", true)]            // 交差 {b}（1 要素でも足りる ＝ 部分集合ではない）
    [InlineData("b,c,d", true)]        // 交差 {b}（許可側に無い要素が在っても失われない）
    [InlineData("a,c", false)]         // 交差なし
    [InlineData("c", false)]
    [InlineData("", false)]            // 🔴 空集合は何にも一致しない
    [InlineData("  ", false)]
    [InlineData(",", false)]           // 区切りだけも空集合である
    public void A_set_valued_key_matches_on_a_non_empty_intersection(string value, bool expected)
        => AttributeFilterMatch.MatchesOne(
                new AttributeFilter(DocumentAttributeEncoding.SharedWithKey, ["b", "x"]), value)
            .Should().Be(expected);

    // 🔴 **部分集合ではない**ことの直接の固定（`ADR-0080` 決定 2）——
    // 「タグを 1 つ足しただけで既存のアクセスが失われる」振る舞いを決定 2 は明示的に退けている。
    [Fact]
    public void Adding_a_member_does_not_take_access_away()
    {
        var filter = new AttributeFilter(DocumentAttributeEncoding.TagsKey, ["sales"]);

        AttributeFilterMatch.MatchesOne(filter, "sales").Should().BeTrue();
        AttributeFilterMatch.MatchesOne(filter, "sales,hr,legal").Should().BeTrue(
            "部分集合判定なら、タグを足した文書が見えなくなる");
    }

    // 分割規則は `UserAttributeEncoding.Split` ただ 1 つである（空白・タブでも切る）。
    [Theory]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a, b")]
    public void The_split_rule_comes_from_the_single_declared_encoding(string value)
        => AttributeFilterMatch.MatchesOne(
                new AttributeFilter(DocumentAttributeEncoding.SharedWithKey, ["b"]), value)
            .Should().BeTrue();

    // ── MatchesAll: フィルタ間 AND・キー欠落は不一致（欠落は安全側に倒す）──────────

    [Fact]
    public void Every_filter_must_pass()
    {
        var attributes = new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            [DocumentAttributeEncoding.SharedWithKey] = "u-alice,g-knowledge",
        };

        AttributeFilterMatch.MatchesAll(attributes,
        [
            new AttributeFilter("confidentiality", ["internal"]),
            new AttributeFilter(DocumentAttributeEncoding.SharedWithKey, ["g-knowledge"]),
        ]).Should().BeTrue();

        // 1 つでも外れれば不一致（AND）。
        AttributeFilterMatch.MatchesAll(attributes,
        [
            new AttributeFilter("confidentiality", ["public"]),
            new AttributeFilter(DocumentAttributeEncoding.SharedWithKey, ["g-knowledge"]),
        ]).Should().BeFalse();
    }

    [Fact]
    public void A_document_without_the_key_does_not_match()
        => AttributeFilterMatch.MatchesAll(
                new Dictionary<string, string> { ["confidentiality"] = "internal" },
                [new AttributeFilter(DocumentAttributeEncoding.SharedWithKey, ["g-knowledge"])])
            .Should().BeFalse("属性キーを持たない文書は不一致（欠落は安全側に倒す）");

    // フィルタが空 ＝ 条件なし（`BffScopeResolver` / `AbacPageFilter` が「全件許可」と読む面）。
    // 🔴 **この意味論があるため、`AbacEvaluator` は許可値が空になった分岐を落とす**（#1447）。
    [Fact]
    public void An_empty_filter_list_matches_everything()
        => AttributeFilterMatch.MatchesAll(new Dictionary<string, string>(), []).Should().BeTrue();

    // ── FR-19, ADR-0098 決定 1: 共有先を属性の像へ重ねる（`WithSharedWith`）─────────

    [Fact]
    public void Shared_targets_are_overlaid_as_a_set_valued_attribute()
    {
        var attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" };

        var view = DocumentAttributeEncoding.WithSharedWith(attributes, ["u-alice", "g-knowledge"]);

        AttributeFilterMatch.MatchesAll(view,
            [new AttributeFilter(DocumentAttributeEncoding.SharedWithKey, ["g-knowledge"])])
            .Should().BeTrue();
        // 🔴 **元の辞書は変更しない**（`DocumentDto.Attributes` は書き戻しの入力にもなる）。
        attributes.Should().NotContainKey(DocumentAttributeEncoding.SharedWithKey);
    }

    // 🔴 **空集合は載せない** —— 載せると「空文字と一致」の余地が生まれる。
    [Fact]
    public void An_empty_share_list_does_not_add_the_key()
    {
        var attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" };

        DocumentAttributeEncoding.WithSharedWith(attributes, null)
            .Should().NotContainKey(DocumentAttributeEncoding.SharedWithKey);
        DocumentAttributeEncoding.WithSharedWith(attributes, [])
            .Should().NotContainKey(DocumentAttributeEncoding.SharedWithKey);
    }
}
