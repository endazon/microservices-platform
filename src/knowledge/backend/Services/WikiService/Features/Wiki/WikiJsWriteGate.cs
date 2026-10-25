using Platform.Shared.Infrastructure.Foundation.Messaging;
using WikiService.Domain.Ports;
using WikiService.Infrastructure.Persistence;

namespace WikiService.Features.Wiki;

// 🔴 FR-13, UC-07, ADR-0011, ADR-0027, [[IADR-0557]] 決定 5, [[IADR-0563]] (#1971), [[IADR-0599]] (#2100): Wiki.js へ同時に書き込む数の上限（全レプリカで合わせて）。
//
// Wiki.js 2.5 の pages.create / pages.delete は木の再構築（rebuild-tree）を、トランザクションを張らずに `pageTree` の
// truncate → insert で走らせる。2 本が並ぶと主キーで衝突して書き込みが失敗する（IADR-0557 の実測: 並列 2 で作成の 2/8・並列 4 で 6/8）。
// 毎分の件数も 1 割強しか増えない（律速は Wiki.js の CPU）。だから**既定は 1（直列）**とする。
//
// - 同期（`DocumentSyncConsumer`）と撤去（`DocumentDeletedConsumer`）は別のキューの別のリスナーで並んで動く。
//   リスナーごとの並びの上限（リスナーの数）だけでは、作成と削除が重なったときの rebuild-tree の並びを止められない。
//   **両方の受け口が同じこの門を通る**ことで、プロセスの中の Wiki.js への書き込みを上限の数に抑える。
// - 門の待ちには期限を付ける（`WikiSyncTimeouts.WriteGateWait`）。前の書き込みは Wiki.js 1 回の期限（`WikiSyncTimeouts.WikiJs`）で
//   必ず切られるので、待ちは「自分より前に並ぶ書き込みの数 × その期限」を超えない。超えたら時間切れ（呼び出し先 `wiki-js-gate`）として投げ、
//   再試行へ委ねる（受け口の実行期限による「取り消し」にしない。#1640 と同じ作法）。
// - ［2026-10-23 / #2100・[[IADR-0599]]］🔴 **門はレプリカをまたいで効く。** helm の既定は HPA（minReplicas 2）で、ローリング更新の
//   maxSurge でも新旧 2 pod が同時に生きる。プロセスの中の枠（`SemaphoreSlim`）を通った後、レプリカをまたぐ枠
//   （`IWikiJsReplicaWriteLock`。本番は PostgreSQL の advisory lock）も取ってから Wiki.js を呼ぶ。両方の枠の取得が「門の待ち」であり、
//   待ちの期限は `WikiSyncTimeouts.WriteGateWait`（既定は Wiki.js 1 回の期限の 2 倍＝2 レプリカの最悪）で測る。
//   プロセスの中の枠を先に取るのは、1 レプリカから DB のロック待ちへ並ぶ受け口を上限の数に抑えるためである。
public sealed class WikiJsWriteGate : IDisposable
{
    public const string MaxParallelWritesKey = "Wiki:MaxParallelWrites";
    public const int DefaultMaxParallelWrites = 1;

    // 上限は実測した範囲（IADR-0557 の並列 1・2・4）に閉じる。範囲の外は起動で止める（黙って丸めない）。
    public const int MaxAllowedParallelWrites = 4;

    private readonly SemaphoreSlim _slots;
    private readonly IWikiJsReplicaWriteLock _replicaLock;

    // プロセスの中の枠だけで抑える門（単体試験の器）。本番の配線は下のレプリカをまたぐ枠つきの形を使う（Program.cs）。
    public WikiJsWriteGate(int maxParallelWrites)
        : this(maxParallelWrites, ProcessLocalWikiJsReplicaWriteLock.Instance)
    {
    }

    public WikiJsWriteGate(int maxParallelWrites, IWikiJsReplicaWriteLock replicaLock)
    {
        ArgumentNullException.ThrowIfNull(replicaLock);
        if (maxParallelWrites is < 1 or > MaxAllowedParallelWrites)
            throw new InvalidOperationException(
                $"{MaxParallelWritesKey}（環境変数 Wiki__MaxParallelWrites）は 1〜{MaxAllowedParallelWrites} でなければならない"
                + $"（与えられた値: {maxParallelWrites}）。Wiki.js 2.5 は書き込みが並ぶと木の再構築が衝突するので、既定は 1 である。");
        MaxParallelWrites = maxParallelWrites;
        _replicaLock = replicaLock;
        _slots = new SemaphoreSlim(maxParallelWrites, maxParallelWrites);
    }

    public int MaxParallelWrites { get; }

    // 構成から上限を読む（範囲の外・整数でない値は起動で止める）。リスナーの数（Program.cs）と門の両方へ同じ値を入れる。
    public static int ReadMaxParallelWrites(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[MaxParallelWritesKey];
        if (string.IsNullOrWhiteSpace(raw)) return DefaultMaxParallelWrites;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException(
                $"{MaxParallelWritesKey}（環境変数 Wiki__MaxParallelWrites）は整数でなければならない（与えられた値: '{raw}'）。");
        if (value is < 1 or > MaxAllowedParallelWrites)
            throw new InvalidOperationException(
                $"{MaxParallelWritesKey}（環境変数 Wiki__MaxParallelWrites）は 1〜{MaxAllowedParallelWrites} でなければならない"
                + $"（与えられた値: {value}）。Wiki.js 2.5 は書き込みが並ぶと木の再構築が衝突するので、既定は 1 である。");
        return value;
    }

    // 門を通ってから Wiki.js を 1 回呼ぶ。門の待ち（プロセスの中の枠 ＋ レプリカをまたぐ枠）と呼び出しは別の期限で測る
    // （待ちが呼び出しの期限を食わない）。
    public async Task RunAsync(
        ConsumerCallTimeouts calls, string step, TimeSpan waitTimeout, TimeSpan callTimeout,
        Func<CancellationToken, Task> call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(calls);
        IAsyncDisposable? replicaLease = null;
        await calls.RunAsync(step, WikiSyncTimeouts.WikiJsGateTarget, waitTimeout, async t =>
        {
            await _slots.WaitAsync(t);
            try
            {
                replicaLease = await _replicaLock.AcquireAsync(MaxParallelWrites, t);
            }
            catch
            {
                _slots.Release();
                throw;
            }
        }, ct);
        try
        {
            await calls.RunAsync(step, WikiSyncTimeouts.WikiJsTarget, callTimeout, call, ct);
        }
        finally
        {
            // レプリカをまたぐ枠を先に返す（プロセスの中の次の受け口が DB のロック待ちへ並べるように）。
            try
            {
                await replicaLease!.DisposeAsync();
            }
            finally
            {
                _slots.Release();
            }
        }
    }

    public void Dispose() => _slots.Dispose();
}
