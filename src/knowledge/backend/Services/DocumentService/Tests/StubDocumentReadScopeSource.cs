using System.Collections.Concurrent;
using DocumentService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;

namespace DocumentService.Tests;

// FR-19, NFR-09, 計画 ADR-0119 決定 3 (#1614): 読み取りの許可（認可サービスの `AuthzScope/Resolve`）のスタブ。
//
// 🔴 **既定は「読めるものは無い」**（null）＝ 本番の縮退（未構成・引けない）と同じ向きである。
// 宣言した利用者にだけ分岐の並びを返す。呼ばれた回数を利用者ごとに数える
// （「所有者・組織文書だけの読み取りでは 1 度も問わない」「要求ごとに高々 1 回」を測るため）。
public sealed class StubDocumentReadScopeSource : IDocumentReadScopeSource
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<IReadOnlyList<AttributeFilter>>> _grants =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

    /// <summary>利用者に `shared_with ∈ values` の分岐（＝共有先ベースの read ポリシー）を 1 本与える。</summary>
    public void GrantSharedWith(string userId, params string[] values)
        => _grants[userId] = [[new AttributeFilter("shared_with", [.. values])]];

    /// <summary>利用者に任意の分岐を与える。</summary>
    public void Grant(string userId, IReadOnlyList<IReadOnlyList<AttributeFilter>> branches)
        => _grants[userId] = branches;

    public int CallsFor(string userId) => _calls.GetValueOrDefault(userId);

    public Task<IReadOnlyList<IReadOnlyList<AttributeFilter>>?> ResolveReadBranchesAsync(
        string userId, CancellationToken ct)
    {
        _calls.AddOrUpdate(userId, 1, (_, n) => n + 1);
        return Task.FromResult(_grants.TryGetValue(userId, out var branches) ? branches : null);
    }
}
