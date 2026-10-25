using AwesomeAssertions;
using Knowledge.Contracts.Ordering;
using Xunit;

namespace Knowledge.Contracts.Tests;

// 🔴 FR-02, IADR-0638 決定 1 (#2150 / #2251): 受け手が共有する前後の判定。鍵は辞書順 (時刻, 連番) で、連番は第 1 の鍵にしない。
// 受け手ごとの P1〜P5（作業仕様書の規則 11 の表）は各サービスの試験が表明する。ここでは関数そのものの性質を固定する。
public class DocumentEventOrderTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 時刻が先に効く_連番が小さくても時刻が新しければ新しい()
    {
        // 作り直し（同じ ID・連番は 0 から）の形。連番を先にすると、ここが逆になる。
        DocumentEventOrder.IsOlder(T.AddSeconds(1), 1, T, 9).Should().BeFalse();
        DocumentEventOrder.IsOlder(T, 9, T.AddSeconds(1), 1).Should().BeTrue();
    }

    [Fact]
    public void 時刻が等しければ連番で比べる()
    {
        DocumentEventOrder.IsOlder(T, 5, T, 6).Should().BeTrue("同時刻の逆転（#2150 の根）");
        DocumentEventOrder.IsOlder(T, 6, T, 5).Should().BeFalse();
    }

    [Fact]
    public void 等しいものは古くない_再配送は反映する()
    {
        DocumentEventOrder.IsOlder(T, 6, T, 6).Should().BeFalse();
        DocumentEventOrder.Compare(T, 6, T, 6).Should().Be(0);
    }

    [Theory]
    [InlineData(null, 3L)]
    [InlineData(3L, null)]
    [InlineData(null, null)]
    public void どちらかの連番が無ければ時刻だけで比べる(long? incoming, long? applied)
    {
        DocumentEventOrder.IsOlder(T, incoming, T, applied).Should().BeFalse("同時刻は区別できず、反映する（現行と同じ）");
        DocumentEventOrder.IsOlder(T.AddTicks(-10), incoming, T, applied).Should().BeTrue("時刻が古ければ古い");
    }
}
