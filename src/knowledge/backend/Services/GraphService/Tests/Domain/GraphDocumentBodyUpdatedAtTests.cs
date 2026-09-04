using AwesomeAssertions;
using GraphService.Domain;

namespace GraphService.Tests.Domain;

// FR-10, UC-05, SC-10, ADR-0006, ADR-0050 決定 2, planning#494 決定 2, [[IADR-0353]] (#1186):
// **本文が変わったときにだけ前進する時刻**の契約を固定する。
//
// 🔴 **本ファイルが守るのは、陳腐化文書数が「自分の改善作業で消えない」ことである。**
// 計画の言い方では「指標が自分の改善作業で消えるなら、それは測定ではない」——
// タグ・属性の整理で `BodyUpdatedAt` が前進すると、**中身が古いままの文書にタグを付け直すだけで
// 件数が減る**。ここが緩むと指標そのものが無意味になる。
//
// 陰性テスト（前進しない）だけでは「一度も前進しない実装」でも緑になるため、
// **陽性対照（指紋が変われば前進する）と必ず対で置く**。
[Trait("TestKind", "Unit")]
public class GraphDocumentBodyUpdatedAtTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    private static GraphDocument Doc(string? hash)
        => GraphDocument.Create(Guid.NewGuid(), "t", [], hash, T0);

    // 新規行の初期値は複製元イベントの更新時刻である（本文の時刻を別に知る手立てが無い）。
    [Fact]
    public void Create_seeds_the_body_time_with_the_event_time()
        => Doc("hash-1").BodyUpdatedAt.Should().Be(T0);

    // 🔴 **陽性対照。** 指紋が変われば前進する。これが無いと下の陰性テストは
    // 「BodyUpdatedAt を一度も更新しない実装」でも緑になる。
    [Fact]
    public void TryApply_advances_the_body_time_when_the_fingerprint_changes()
    {
        var doc = Doc("hash-1");
        var later = T0.AddDays(200);

        doc.TryApply("t", [], "hash-2", later).Should().BeTrue();

        doc.BodyUpdatedAt.Should().Be(later, "本文が変わった。陳腐化の起点はここで前進する");
        doc.UpdatedAt.Should().Be(later);
    }

    // 🔴 **本裁定の中心（受け入れ基準 3）。** タグ・属性だけの更新では前進しない。
    // 発行側（`Document.UpdateMetadata` / `Document.Update`）は `Touch()` を呼ぶため
    // **`UpdatedAt` は前進する** —— 両者が別々に動くことを 1 つの表明で固定する。
    [Fact]
    public void TryApply_does_not_advance_the_body_time_for_a_metadata_only_update()
    {
        var doc = Doc("hash-1");
        var later = T0.AddDays(200);

        // 同じ指紋（本文は不変）・新しい属性・新しい更新時刻＝メタデータのみの更新。
        doc.TryApply("t", new Dictionary<string, string> { ["confidentiality"] = "restricted" },
            "hash-1", later).Should().BeTrue();

        doc.UpdatedAt.Should().Be(later, "メタデータ更新でも複製の更新時刻は前進する");
        doc.BodyUpdatedAt.Should().Be(T0,
            "🔴 タグ整理で陳腐化文書数が減ってはならない（planning#494 決定 2）");
    }

    // 指紋 null は「指紋化できなかった＝**不明**」であり、変更ではない
    // （GraphDocumentSyncConsumer が却下解除・リンク抽出で採っているのと同じ向き）。
    [Fact]
    public void TryApply_treats_an_absent_fingerprint_as_unknown_not_as_a_change()
    {
        var doc = Doc("hash-1");

        doc.TryApply("t", [], null, T0.AddDays(200)).Should().BeTrue();

        doc.BodyUpdatedAt.Should().Be(T0, "不明を「変わった」と読むと誤って前進する");
    }

    // 順序ガードが先に効く（古いイベントでは何も変わらない）。
    [Fact]
    public void TryApply_keeps_the_body_time_when_the_event_is_stale()
    {
        var doc = Doc("hash-1");
        doc.TryApply("t", [], "hash-2", T0.AddDays(10));

        doc.TryApply("t", [], "hash-3", T0.AddDays(5)).Should().BeFalse();

        doc.BodyUpdatedAt.Should().Be(T0.AddDays(10));
    }

    // 同一指紋の再配信（冪等）。適用はされるが本文の時刻は動かない。
    [Fact]
    public void TryApply_does_not_advance_the_body_time_on_a_redelivery()
    {
        var doc = Doc("hash-1");

        doc.TryApply("t", [], "hash-1", T0.AddDays(1)).Should().BeTrue();

        doc.BodyUpdatedAt.Should().Be(T0);
    }
}
