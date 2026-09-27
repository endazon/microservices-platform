using DocumentService.Features.Documents.ContentAbac;

namespace DocumentService.Tests;

// FR-05, NFR-09, 計画 ADR-0121 決定 2・4・5 (#1615): 内容の ABAC の門のスタブ。
//
// 🔴 **既定は閉じている**（本番の既定 `ContentAbac:Mode=Off` と同じ向き）。開くのは、門が開いた後の読み取りの判定を測る試験だけである。
// 門そのもの（構成・ポリシーの確認・ラッチ）は `ContentAbacGateTests` が本物で測る。ここは「門を読む側」を測るための代役である。
// `Reads` は `IsOpen` が読まれた回数（「要求の中で 1 度だけ読む」を測るため）。
public sealed class StubContentAbacGate : IContentAbacGate
{
    private int _reads;
    private volatile bool _open;

    public StubContentAbacGate(bool open = false) => _open = open;

    public int Reads => Volatile.Read(ref _reads);

    public void Open() => _open = true;

    public bool IsOpen
    {
        get
        {
            Interlocked.Increment(ref _reads);
            return _open;
        }
    }

    public ContentAbacGateState State => _open ? ContentAbacGateState.Open : ContentAbacGateState.Disabled;
}
