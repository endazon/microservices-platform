using AwesomeAssertions;
using GraphService.Domain;

namespace GraphService.Tests.Domain;

// FR-18, ADR-0033 決定 7・10: AI 提案の 3 状態遷移（#914）。
//
// **却下解除のロジックはここで固定する。** 発火（本文の変更を受け取ること）は #911 が配線し、
// `GraphDocumentSyncConsumer` が指紋の変化を見て `TryReinstate` を呼ぶ。
// **「ロジックが正しいこと」と「実際に動くこと」は別であり、ここで測れるのは前者だけである**
// （後者は `GraphDocumentSyncConsumer` 側のテストが測る）。
//
// 🔴 **［2026-08-28 追記 / #438］「その未配線は `AiSuggestionWiringTests` が機械で固定している」
// と書いてあったが、そのテストは一度も存在しなかった**（クラス定義が全域で 0 件）。
// 配線が済んだ今は前提そのものも古い。**機械が守っていると書くときは、その機械を指させること。**
[Trait("TestKind", "Unit")]
public class AiSuggestionStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);

    private static AiSuggestion Link()
        => AiSuggestion.CreateLink(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "根拠", T0);

    // S-01: pending からの正規の遷移は通る。
    [Fact]
    public void Pending_can_be_approved_or_rejected()
    {
        Link().TryApprove().Should().BeTrue();

        var b = Link();
        b.TryReject("s1", "t1", T0).Should().BeTrue();
        b.State.Should().Be(SuggestionState.Rejected);
    }

    // S-02: 🔴 不正な遷移は拒む。
    //
    // 承認済みを再承認すると辺が二重に生まれ、却下済みを承認 UI を経ずに承認できると
    // 却下の記録（再提示の抑止）が黙って壊れる。
    [Theory]
    [InlineData(true)]  // approved から
    [InlineData(false)] // rejected から
    public void Non_pending_states_reject_every_transition(bool approveFirst)
    {
        var s = Link();
        if (approveFirst) s.TryApprove();
        else s.TryReject("s1", "t1", T0);

        var before = s.State;
        s.TryApprove().Should().BeFalse();
        s.TryReject("s2", "t2", T0).Should().BeFalse();
        s.State.Should().Be(before, "不正な遷移は状態を一切変えてはならない");
    }

    // S-06: 却下のたびに回数が増える。
    //
    // ADR-0033 §結果 フォローアップが「同一の組み合わせが 3 回以上却下された件数」を
    // SC-10 で観測すると定めており、**後から数え直せない値**なので最初から数える。
    [Fact]
    public void Rejection_count_accumulates_across_cycles()
    {
        var s = Link();

        s.TryReject("s1", "t1", T0);
        s.RejectedCount.Should().Be(1);

        // 本文が変わって解除 → 再び却下。
        s.TryReinstate("s2", "t1", T0.AddDays(1)).Should().BeTrue();
        s.TryReject("s2", "t1", T0.AddDays(2));
        s.RejectedCount.Should().Be(2, "回数は周期をまたいで累積する（3 回以上の観測に要る）");
    }

    // S-07 陽性対照: 指紋が変われば pending へ戻り、**どちらの端点が変わったか**が残る。
    [Theory]
    [InlineData("s2", "t1", "source")]
    [InlineData("s1", "t2", "target")]
    [InlineData("s2", "t2", "both")]
    public void A_changed_fingerprint_reinstates_with_a_reason(string src, string tgt, string reason)
    {
        var s = Link();
        s.TryReject("s1", "t1", T0);

        s.TryReinstate(src, tgt, T0.AddDays(1)).Should().BeTrue();

        s.State.Should().Be(SuggestionState.Pending);
        s.ReinstatedReason.Should().Be(reason,
            "画面仕様は再提示に理由を必ず添えると定める。『文書が更新された』だけでは "
            + "利用者は何を見直せばよいか判らない");
        s.ReinstatedAt.Should().Be(T0.AddDays(1));
    }

    // S-08 🔴 陽性対照 S-07 の対: 変わっていなければ解除しない。
    //
    // **S-07 だけでは「常に解除する実装」を検出できない。** 対で置いて初めて意味を持つ。
    [Fact]
    public void An_unchanged_fingerprint_does_not_reinstate()
    {
        var s = Link();
        s.TryReject("s1", "t1", T0);

        s.TryReinstate("s1", "t1", T0.AddDays(1)).Should().BeFalse();

        s.State.Should().Be(SuggestionState.Rejected);
        s.ReinstatedAt.Should().BeNull();
        s.ReinstatedReason.Should().BeNull();
    }

    // S-09: 解除は**削除ではなく無効化**。却下の記録が残る。
    [Fact]
    public void Reinstatement_records_the_release_instead_of_erasing_the_rejection()
    {
        var s = Link();
        s.TryReject("s1", "t1", T0);
        s.TryReinstate("s2", "t1", T0.AddDays(3));

        s.RejectedAt.Should().Be(T0, "いつ却下したかが残る");
        s.ReinstatedAt.Should().Be(T0.AddDays(3), "いつ解除したかが残る");
        s.ReinstatedReason.Should().Be("source", "何によって解除されたかが残る");
        s.RejectedCount.Should().Be(1);
    }

    // 解除後は指紋が新しい値へ進む。
    //
    // 進めないと、次の更新で「変わっていない」と誤判定する（比較対象が古い指紋のまま固定される）。
    [Fact]
    public void Reinstatement_advances_the_stored_fingerprints()
    {
        var s = Link();
        s.TryReject("s1", "t1", T0);
        s.TryReinstate("s2", "t1", T0.AddDays(1));
        s.TryReject("s2", "t1", T0.AddDays(2));

        // 同じ "s2" で解除されない＝指紋が進んでいる。
        s.TryReinstate("s2", "t1", T0.AddDays(3)).Should().BeFalse();
        // 次の変更ではきちんと解除される。
        s.TryReinstate("s3", "t1", T0.AddDays(4)).Should().BeTrue();
    }

    // pending / approved からは解除できない（解除は却下の取り消しである）。
    [Fact]
    public void Only_a_rejected_suggestion_can_be_reinstated()
    {
        Link().TryReinstate("x", "y", T0).Should().BeFalse();

        var approved = Link();
        approved.TryApprove();
        approved.TryReinstate("x", "y", T0).Should().BeFalse();
    }

    // タグ提案は端点が 1 つ。**target 側の指紋で誤判定しない。**
    [Fact]
    public void A_tag_suggestion_only_considers_its_single_endpoint()
    {
        var s = AiSuggestion.CreateTag(Guid.NewGuid(), "設計", "根拠", T0);
        s.TryReject("s1", null, T0);

        // target 側に何を渡しても、端点が無いので解除されない。
        s.TryReinstate("s1", "whatever", T0.AddDays(1)).Should().BeFalse();
        // 自分の端点が変われば解除される。
        s.TryReinstate("s2", null, T0.AddDays(2)).Should().BeTrue();
        s.ReinstatedReason.Should().Be("source");
    }

    // 自己ループの提案は起こせない。
    [Fact]
    public void A_self_loop_link_suggestion_is_rejected_at_construction()
    {
        var id = Guid.NewGuid();
        var act = () => AiSuggestion.CreateLink(id, id, Guid.NewGuid(), "r", T0);
        act.Should().Throw<ArgumentException>();
    }
}
