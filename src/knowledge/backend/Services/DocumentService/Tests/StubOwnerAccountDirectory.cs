using System.Collections.Concurrent;
using DocumentService.Domain.Ports;

namespace DocumentService.Tests;

// FR-20, SC-17, NFR-14, 計画 ADR-0114 決定 1・2, [[IADR-0474]] (#1532):
// 同期トークンの所有者のアカウント状態のスタブ。
//
// 🔴 **既定は `Enabled` であり、本番の縮退（常に `Unknown` ＝ 401）と逆である。**
// 同期を使う試験は 15 本のクラスに散っており、その大半はアカウント状態を問うていない。
// 既定を `Unknown` にすると、それらが「無効化の門」の側で赤になり、各試験が測りたいものが見えなくなる。
// **本番の向きは、スタブへ差し替えないファクトリで別に固定する**
// （`SyncTokenAccountStateTests` の「口が構成されていない配備では同期トークンが通らない」）。
public sealed class StubOwnerAccountDirectory : IOwnerAccountDirectory
{
    private readonly ConcurrentDictionary<string, OwnerAccountState> _known = new(StringComparer.Ordinal);

    // 照会された所有者（「端末が有効と確定した後だけ・所有者の ID で引く」ことを測る）。
    public ConcurrentQueue<string> Queried { get; } = new();

    public void Declare(string ownerId, OwnerAccountState state) => _known[ownerId] = state;

    public Task<OwnerAccountState> GetStateAsync(string ownerId, CancellationToken ct)
    {
        Queried.Enqueue(ownerId);
        return Task.FromResult(_known.TryGetValue(ownerId, out var state) ? state : OwnerAccountState.Enabled);
    }
}
