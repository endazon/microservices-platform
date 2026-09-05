using AwesomeAssertions;
using GraphService.Domain;

namespace GraphService.Tests.Domain;

// FR-18, ADR-0051 決定 1, IADR-0380 (#1244): 語の共起による類似度（純粋・決定的）。
//
// 🔴 **否定形テストには必ず陽性対照を対で置く。** 「候補に入らない」だけでは、そもそも何も返さない実装
// （#1244 の欠陥そのもの）でも緑になる。T-41（入る）と T-42 / T-43（入らない）は同じ母集合で測る。
[Trait("TestKind", "Unit")]
public class TermProfileTests
{
    private static readonly Guid Origin = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Similar = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Unrelated = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid Decoy = Guid.Parse("00000000-0000-0000-0000-00000000000d");

    // 全文書に共通する定型句（社内文書の書き出し）。**これだけを共有する文書は似ていない。**
    private const string Boilerplate = "本書は社内向けの文書である。";

    private static IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>> Corpus()
        => new Dictionary<Guid, IReadOnlyDictionary<string, int>>
        {
            [Origin] = TermProfile.Extract(
                "知識グラフの ABAC 判定設計",
                Boilerplate + "ホップごとに認可述語を評価し、不許可ノードでは探索を打ち切る。属性の複製はイベントで追随する。"),
            [Similar] = TermProfile.Extract(
                "グラフ探索の認可レビュー",
                Boilerplate + "探索はホップごとに認可述語を評価する。不許可ノードは打ち切り、属性の複製で判定する。"),
            [Unrelated] = TermProfile.Extract(
                "Quarterly budget planning",
                "Revenue forecast and headcount plan for the fiscal year. Travel expenses are capped."),
            [Decoy] = TermProfile.Extract(
                "厚生施設の利用案内",
                Boilerplate + "食堂の営業時間と駐車場の利用手続きを案内する。予約は総務へ申し込む。"),
        };

    // T-41 陽性: 本文を共有する文書が候補になり、最上位に並ぶ。
    [Fact]
    public void Document_sharing_topical_terms_ranks_first()
    {
        var ranked = TermProfile.Rank(Origin, Corpus(), minScore: 0.1, limit: 10);

        ranked.Should().NotBeEmpty("#1244: 供給元が 1 件も返さない状態を赤にする");
        ranked[0].DocumentId.Should().Be(Similar);
        // 実測 0.335（2-gram の共有。語順・助詞の違いで 1.0 にはならない）。しきい値 0.1 を明確に超える。
        ranked[0].Score.Should().BeGreaterThan(0.25);
    }

    // T-42 陰性: 語彙を共有しない文書は候補に入らない（T-41 と同じ母集合）。
    [Fact]
    public void Document_sharing_no_terms_is_not_a_candidate()
    {
        var ranked = TermProfile.Rank(Origin, Corpus(), minScore: 0.1, limit: 10);

        ranked.Select(r => r.DocumentId).Should().NotContain(Unrelated);
    }

    // T-43 🔴 陰性（変異検出）: 全文書に共通する定型句だけを共有する文書はしきい値を下回る。
    //
    // **IDF を外すと（重みを 1 に固定すると）落ちる** —— 定型句の 2-gram が全文書に在っても素の共起では
    // 似て見える。日本語の機能語（です・ます・こと）で「どの文書も互いに似る」形を塞いでいるのはこの重みである。
    [Fact]
    public void Document_sharing_only_boilerplate_falls_below_the_threshold()
    {
        var ranked = TermProfile.Rank(Origin, Corpus(), minScore: 0.1, limit: 10);

        ranked.Select(r => r.DocumentId).Should().NotContain(Decoy,
            "定型句だけの共有は IDF で落ちる（外すと 0.1 を超えて候補に入る）");
        // 陽性対照: 同じ母集合で Similar は入っている（T-41 と二重に置くのは、この 1 本だけ見ても
        // 「何も返していない」と区別できるようにするため）。
        ranked.Select(r => r.DocumentId).Should().Contain(Similar);
    }

    // T-44 決定性: 同じ入力から同じ順序・同じスコアが出る。
    [Fact]
    public void Ranking_is_deterministic()
    {
        var first = TermProfile.Rank(Origin, Corpus(), minScore: 0.0, limit: 10);
        var second = TermProfile.Rank(Origin, Corpus(), minScore: 0.0, limit: 10);

        first.Should().Equal(second);
    }

    // 起点自身は返さない・limit を守る。
    [Fact]
    public void Origin_itself_is_never_returned_and_limit_is_respected()
    {
        var ranked = TermProfile.Rank(Origin, Corpus(), minScore: 0.0, limit: 1);

        ranked.Should().HaveCount(1);
        ranked.Select(r => r.DocumentId).Should().NotContain(Origin);
    }

    // 語を持たない起点・母集合に無い起点は空。
    [Fact]
    public void Origin_without_terms_or_outside_the_corpus_yields_nothing()
    {
        var corpus = new Dictionary<Guid, IReadOnlyDictionary<string, int>>(Corpus())
        {
            [Origin] = TermProfile.Extract("", null),
        };

        TermProfile.Rank(Origin, corpus, 0.0, 10).Should().BeEmpty();
        TermProfile.Rank(Guid.NewGuid(), Corpus(), 0.0, 10).Should().BeEmpty();
    }

    // 語の切り方: CJK は 2-gram、それ以外は小文字化した 2 文字以上の語、表題は重い。
    [Fact]
    public void Extract_splits_cjk_into_bigrams_and_lowercases_latin_words()
    {
        var terms = TermProfile.Extract("ABAC 判定", "The ABAC predicate. x 1");

        terms.Should().ContainKey("abac").WhoseValue.Should().Be(TermProfile.TitleWeight + 1,
            "表題の 1 出現 × 3 ＋ 本文の 1 出現");
        terms.Should().ContainKey("判定").WhoseValue.Should().Be(TermProfile.TitleWeight);
        terms.Should().ContainKey("predicate");
        terms.Should().NotContainKey("x", "1 文字の語は持たない");
        terms.Should().NotContainKey("1");
        terms.Should().NotContainKey("The", "小文字化する");
    }

    // 上位 maxTerms 語に切る（同数は語の順序。決定的）。
    [Fact]
    public void Extract_keeps_only_the_most_frequent_terms()
    {
        var body = string.Join(' ', Enumerable.Range(0, 300).Select(i => $"term{i:D3}"));

        var terms = TermProfile.Extract(null, body + " term000 term000", maxTerms: 5);

        terms.Should().HaveCount(5);
        terms.Should().ContainKey("term000").WhoseValue.Should().Be(3);
    }
}
