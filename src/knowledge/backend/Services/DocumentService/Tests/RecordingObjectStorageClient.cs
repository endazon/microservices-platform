using System.Collections.Concurrent;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Tests;

// FR-21, ADR-0014/ADR-0015（Superseded by ADR-0106）: オブジェクトストレージ（SeaweedFS）の記録用スタブ。
//
// **`NullObjectStorageClient`（本番の縮退実装）では受け入れ基準 ⑦ を検証できない** ——
// あちらは決定的な URI を返すだけで本文を保持しないため、「1 MB 以下の本文が
// **切り詰められることなく**格納された」ことを測る手段が無い。ここでは格納した本文を保持し、
// **入力と 1 バイトも違わないこと**をテストが直接見られるようにする。
public sealed class RecordingObjectStorageClient : IObjectStorageClient
{
    public const string Bucket = "knowledge-normalized";

    // 参照 URI → 格納した本文。
    public ConcurrentDictionary<string, string> Texts { get; } = new();

    // 格納の呼び出し回数。**「拒否したのに保存していた」を検出する**ために数える。
    public int PutTextCallCount;

    // FR-06, [[IADR-0555]] (#1973): 書き込みの失敗注入（キーを受け取り、例外を返せばそれを投げる）。
    // **器を共有するので、使った試験は最後に null へ戻す。**
    public Func<string, Exception?>? PutTextThrows { get; set; }

    public Task<string> PutTextAsync(string key, string text, string contentType,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref PutTextCallCount);
        if (PutTextThrows?.Invoke(key) is { } injected)
            throw injected;
        var uri = StorageUri.Build(Bucket, key);
        Texts[uri] = text;
        return Task.FromResult(uri);
    }

    public Task<string> PutBytesAsync(string key, byte[] bytes, string contentType,
        CancellationToken ct = default) =>
        Task.FromResult(StorageUri.Build(Bucket, key));

    // FR-06, FR-19, ADR-0057 決定 1, IADR-0296: **何が消されたかを記録する。**
    // 削除の伝播は「呼ばれたか」ではなく「台帳から逆引きした URI が全部揃っているか」で見るため、
    // 呼び出し回数ではなく **URI の集合**を残す（重複呼び出しも観測できるようリストで持つ）。
    public List<string> Deleted { get; } = [];

    // 失敗注入。**fail-closed（オブジェクトが消せないなら DB 行を消さない）を測るための唯一の口**である。
    // 実クライアントの障害を器で再現する手段が他に無い（Docker 非依存で試験するため）。
    public Func<string, bool>? FailDeleteWhen { get; set; }

    // #1608: 例外の種類まで選ぶ失敗注入（時間切れ＝`TaskCanceledException` と、呼び出し側の取り消しを
    // 区別して測るため）。null を返した URI は通常どおり消す。`FailDeleteWhen` より先に見る。
    public Func<string, Exception?>? DeleteThrows { get; set; }

    public Task DeleteAsync(string uri, CancellationToken ct = default)
    {
        if (DeleteThrows?.Invoke(uri) is { } injected)
            throw injected;
        if (FailDeleteWhen?.Invoke(uri) == true)
            throw new InvalidOperationException($"注入した削除失敗: {uri}");

        lock (Deleted) Deleted.Add(uri);
        Texts.TryRemove(uri, out _);
        return Task.CompletedTask;
    }

    // 記録の掃除（テスト間で器を共有する IClassFixture のため）。
    public void ResetDeletions()
    {
        lock (Deleted) Deleted.Clear();
        FailDeleteWhen = null;
        DeleteThrows = null;
    }

    // FR-06, [[IADR-0544]] (#1905): 読み取りの失敗注入（S3 の NoSuchKey / NoSuchBucket を器で再現する唯一の口）。
    // null を返した URI は通常どおり読む。**器を共有するので、使った試験は最後に null へ戻す。**
    public Func<string, Exception?>? GetTextThrows { get; set; }

    // FR-06, [[IADR-0549]] (#1931 項目 3): 本文を読んだ回数（存在確認が本文を読まないことを測る）。
    public int GetTextCallCount;

    public Task<string> GetTextAsync(string uri, CancellationToken ct = default)
    {
        Interlocked.Increment(ref GetTextCallCount);
        if (GetTextThrows?.Invoke(uri) is { } injected)
            throw injected;
        return Task.FromResult(Texts.TryGetValue(uri, out var text)
            ? text
            : throw new KeyNotFoundException(uri));
    }

    // FR-06, [[IADR-0549]] (#1931 項目 3): 存在確認。格納した本文があれば在る、無ければ NoSuchKey として「無い」。
    // 失敗注入（`ExistsThrows`）は NoSuchBucket・時間切れなど「無い」に倒してはならない失敗を再現する。
    // **器を共有するので、使った試験は最後に null へ戻す。** 確かめた URI は `ExistsCalls` に残す。
    public Func<string, Exception?>? ExistsThrows { get; set; }

    public ConcurrentQueue<string> ExistsCalls { get; } = new();

    public Task<bool> ExistsAsync(string uri, CancellationToken ct = default)
    {
        ExistsCalls.Enqueue(uri);
        if (ExistsThrows?.Invoke(uri) is { } injected)
            throw injected;
        return Task.FromResult(Texts.ContainsKey(uri));
    }

    public Task<byte[]> GetBytesAsync(string uri, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());

    public bool CanResolve(string? uri) => uri is not null && Texts.ContainsKey(uri);

    public string CreatePresignedGetUrl(string uri, TimeSpan? expiry = null) => uri;
}
