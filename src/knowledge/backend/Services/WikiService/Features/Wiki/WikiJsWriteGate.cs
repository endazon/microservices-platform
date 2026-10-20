using Platform.Shared.Infrastructure.Foundation.Messaging;

namespace WikiService.Features.Wiki;

// 🔴 FR-13, UC-07, ADR-0011, ADR-0027, [[IADR-0557]] 決定 5, [[IADR-0563]] (#1971): Wiki.js へ同時に書き込む数の上限（プロセスに 1 つ）。
//
// Wiki.js 2.5 の pages.create / pages.delete は木の再構築（rebuild-tree）を、トランザクションを張らずに `pageTree` の
// truncate → insert で走らせる。2 本が並ぶと主キーで衝突して書き込みが失敗する（IADR-0557 の実測: 並列 2 で作成の 2/8・並列 4 で 6/8）。
// 毎分の件数も 1 割強しか増えない（律速は Wiki.js の CPU）。だから**既定は 1（直列）**とする。
//
// - 同期（`DocumentSyncConsumer`）と撤去（`DocumentDeletedConsumer`）は別のキューの別のリスナーで並んで動く。
//   リスナーごとの並びの上限（リスナーの数）だけでは、作成と削除が重なったときの rebuild-tree の並びを止められない。
//   **両方の受け口が同じこの門を通る**ことで、プロセスの中の Wiki.js への書き込みを上限の数に抑える。
// - 門の待ちには、Wiki.js の 1 回の呼び出しと同じ長さの期限を付ける（`WikiSyncTimeouts.WikiJs`）。前の書き込みは
//   その期限で必ず切られるので、上限 1 なら待ちはその長さを超えない。超えたら時間切れ（呼び出し先 `wiki-js-gate`）として投げ、
//   再試行へ委ねる（受け口の実行期限による「取り消し」にしない。#1640 と同じ作法）。
// - 🔴 **門はプロセスの中だけで効く。** wiki-service のレプリカを 2 以上にすると、レプリカの数だけ並ぶ（helm の
//   `services.wiki.replicas` は 1）。
public sealed class WikiJsWriteGate : IDisposable
{
    public const string MaxParallelWritesKey = "Wiki:MaxParallelWrites";
    public const int DefaultMaxParallelWrites = 1;

    // 上限は実測した範囲（IADR-0557 の並列 1・2・4）に閉じる。範囲の外は起動で止める（黙って丸めない）。
    public const int MaxAllowedParallelWrites = 4;

    private readonly SemaphoreSlim _slots;

    public WikiJsWriteGate(int maxParallelWrites)
    {
        if (maxParallelWrites is < 1 or > MaxAllowedParallelWrites)
            throw new InvalidOperationException(
                $"{MaxParallelWritesKey}（環境変数 Wiki__MaxParallelWrites）は 1〜{MaxAllowedParallelWrites} でなければならない"
                + $"（与えられた値: {maxParallelWrites}）。Wiki.js 2.5 は書き込みが並ぶと木の再構築が衝突するので、既定は 1 である。");
        MaxParallelWrites = maxParallelWrites;
        _slots = new SemaphoreSlim(maxParallelWrites, maxParallelWrites);
    }

    public int MaxParallelWrites { get; }

    public static WikiJsWriteGate From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[MaxParallelWritesKey];
        if (string.IsNullOrWhiteSpace(raw)) return new WikiJsWriteGate(DefaultMaxParallelWrites);
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException(
                $"{MaxParallelWritesKey}（環境変数 Wiki__MaxParallelWrites）は整数でなければならない（与えられた値: '{raw}'）。");
        return new WikiJsWriteGate(value);
    }

    // 門を通ってから Wiki.js を 1 回呼ぶ。門の待ちと呼び出しは別の期限で測る（待ちが呼び出しの期限を食わない）。
    public async Task RunAsync(
        ConsumerCallTimeouts calls, string step, TimeSpan timeout,
        Func<CancellationToken, Task> call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(calls);
        await calls.RunAsync(step, WikiSyncTimeouts.WikiJsGateTarget, timeout, t => _slots.WaitAsync(t), ct);
        try
        {
            await calls.RunAsync(step, WikiSyncTimeouts.WikiJsTarget, timeout, call, ct);
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();
}
