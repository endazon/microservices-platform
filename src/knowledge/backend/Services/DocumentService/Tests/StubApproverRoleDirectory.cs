using System.Collections.Concurrent;
using DocumentService.Domain.Ports;

namespace DocumentService.Tests;

// FR-18, NFR-09, [[IADR-0410]] 追記 2 (#1636): 承認者が管理者かを答える認可サービスの代役。
// 🔴 **既定は NotAdmin**（器が意見を持たない利用者を管理者にしない）。試験は `States` で答えを決め、`Asked` で
//   「問い合わせが実際に走ったか」を観測する（所有者の承認・個人資料では走らないことを測る）。
public sealed class StubApproverRoleDirectory : IApproverRoleDirectory
{
    public ConcurrentDictionary<string, ApproverAdminState> States { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Asked { get; } = new();

    public Task<ApproverAdminState> GetAdminStateAsync(string username, CancellationToken ct)
    {
        Asked.Enqueue(username);
        return Task.FromResult(States.TryGetValue(username, out var state) ? state : ApproverAdminState.NotAdmin);
    }

    public void Reset()
    {
        States.Clear();
        Asked.Clear();
    }
}
