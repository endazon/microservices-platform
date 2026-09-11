using System.Collections.Concurrent;
using DocumentService.Domain.Ports;

namespace DocumentService.Tests;

// FR-19, SC-19, ADR-0096 決定 1・2, [[IADR-0428]] 決定 3, [[IADR-0431]] (#1409):
// 退職の窓の照会のスタブ。
//
// 🔴 **既定は「引けなかった」（`null`）である。** 宣言していない所有者の資料が
// 他のテストの周期で巻き添えに消えないための向きであり、**本番の縮退と同じ向き**でもある
// （口が無い＝削除しない）。陽性を測りたいテストだけが明示的に宣言する。
public sealed class StubOwnerRetentionDirectory : IOwnerRetentionDirectory
{
    private readonly ConcurrentDictionary<string, OwnerRetentionStatus> _known = new(StringComparer.Ordinal);

    // 照会された所有者（「所有者ごとに 1 回だけ引く」ことを測る）。
    public ConcurrentQueue<string> Queried { get; } = new();

    public void Declare(string ownerId, OwnerRetentionStatus status) => _known[ownerId] = status;

    /// <summary>退職済みで窓が閉じた所有者（**削除される唯一の組み合わせ**）。</summary>
    public void DeclareDeparted(string ownerId)
        => Declare(ownerId, new OwnerRetentionStatus(true, false, OwnerRetentionEligibility.Elapsed));

    /// <summary>無効化済みだが窓の中（削除しない）。</summary>
    public void DeclareWithinWindow(string ownerId)
        => Declare(ownerId, new OwnerRetentionStatus(true, false, OwnerRetentionEligibility.WithinWindow));

    /// <summary>無効化済みだが起点が無い／読めない（削除しない）。</summary>
    public void DeclareNotEvaluable(string ownerId)
        => Declare(ownerId, new OwnerRetentionStatus(true, false, OwnerRetentionEligibility.NotEvaluable));

    /// <summary>在籍中（削除しない）。窓の判定が `Elapsed` でも消えないことを測るために値を選べる。</summary>
    public void DeclareActive(string ownerId,
        OwnerRetentionEligibility eligibility = OwnerRetentionEligibility.NotEvaluable)
        => Declare(ownerId, new OwnerRetentionStatus(true, true, eligibility));

    public Task<OwnerRetentionStatus?> GetAsync(string ownerId, CancellationToken ct)
    {
        Queried.Enqueue(ownerId);
        return Task.FromResult(_known.TryGetValue(ownerId, out var status) ? status : null);
    }

    public void Reset()
    {
        _known.Clear();
        Queried.Clear();
    }
}
