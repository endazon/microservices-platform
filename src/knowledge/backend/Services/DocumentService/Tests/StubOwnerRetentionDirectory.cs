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

    // #1583: 呼ばれた回ごとに答えを変える所有者（判定の後・削除の直前の読み直しの間に状態が変わる状況）。
    // 先頭から 1 つずつ消費し、**最後の 1 つは残し続ける**。
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<OwnerRetentionStatus?>>> _sequences =
        new(StringComparer.Ordinal);

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

    /// <summary>
    /// #1583: 1 回目・2 回目…の照会に順に答える（例外を投げる答えも置ける）。尽きたら最後の答えを返し続ける。
    /// 🔴 スタブはクラス内の試験で共有され、後続の試験の周期もこの所有者を引く —— **最後の答えに例外を置かない。**
    /// </summary>
    public void DeclareSequence(string ownerId, params Func<OwnerRetentionStatus?>[] answers)
    {
        if (answers.Length == 0) throw new ArgumentException("答えが要る", nameof(answers));
        _sequences[ownerId] = new ConcurrentQueue<Func<OwnerRetentionStatus?>>(answers);
    }

    /// <summary>退職済みで窓が閉じた所有者の答え（<see cref="DeclareSequence"/> 用）。</summary>
    public static OwnerRetentionStatus Departed()
        => new(true, false, OwnerRetentionEligibility.Elapsed);

    public Task<OwnerRetentionStatus?> GetAsync(string ownerId, CancellationToken ct)
    {
        Queried.Enqueue(ownerId);
        if (_sequences.TryGetValue(ownerId, out var sequence))
        {
            Func<OwnerRetentionStatus?>? answer;
            if (sequence.Count > 1) sequence.TryDequeue(out answer);
            else sequence.TryPeek(out answer);
            return Task.FromResult(answer!());
        }
        return Task.FromResult(_known.TryGetValue(ownerId, out var status) ? status : null);
    }

    public void Reset()
    {
        _known.Clear();
        _sequences.Clear();
        Queried.Clear();
    }
}
