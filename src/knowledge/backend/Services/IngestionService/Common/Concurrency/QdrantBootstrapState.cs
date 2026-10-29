namespace IngestionService.Common.Concurrency;

// FR-02, FR-03, [[IADR-0650]] (#2283): Qdrant のコレクション・索引の保証（`QdrantBootstrapHostedService`）が**一度でも成功したか**。
// readiness（`QdrantBootstrapHealthCheck`）と、コレクションを前提にする後続の器（2-gram の後付け・属性キーの索引の発見・
// 埋め込み待ちの埋め直し）が読む。保証は失敗すると再試行するので、**登録順だけでは前後が決まらない**ためにこれを待つ。
// プロセスで 1 つ（singleton）。一度立てたら戻さない（保証は冪等で、作ったコレクションは消えない前提）。
//
// 🔴 ［#2311 のレビュー］**受け口（取り込み・削除）も保証の成功を待つ。** 保証の器は `BackgroundService` で起動を塞がないので、
// 後から登録された Wolverine の受信が保証より先に始まる（実測: 登録順は 保証 → 後続の器 → Wolverine、`ServicesStartConcurrently=false`）。
// 待たずに書くとコレクションが無く失敗し、再試行の鎖（2・10・30 秒の 4 試行、計 42 秒の待ち）を Qdrant の停止が超えるとデッドレターへ落ちる。
// 待ちには上限（`ConsumerWaitLimit`）を置き、超えたら例外で再試行の鎖へ返す（ブローカの未確認の上限と受け口の実行期限の中に収める）。
public sealed class QdrantBootstrapState(TimeSpan? consumerWaitLimit = null)
{
    /// <summary>受け口が 1 試行の中で保証の成功を待つ上限（既定 60 秒。保証の再試行の待ちの上限 30 秒の 2 倍）。</summary>
    public static readonly TimeSpan DefaultConsumerWaitLimit = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _consumerWaitLimit = consumerWaitLimit ?? DefaultConsumerWaitLimit;
    private readonly TaskCompletionSource _ensured = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsEnsured => _ensured.Task.IsCompleted;

    public void MarkEnsured() => _ensured.TrySetResult();

    /// <summary>保証が成功するまで待つ。成功したら true、先に停止要求が来たら false（例外は上げない）。</summary>
    public async Task<bool> WaitUntilEnsuredAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _ensured.Task.WaitAsync(stoppingToken);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// 受け口の入口で、保証の成功を上限まで待つ。上限を超えたら <see cref="QdrantBootstrapPendingException"/> を投げる
    /// （Wolverine の再試行の鎖へ返す。黙って書きに行かない）。
    /// </summary>
    public async Task WaitForConsumerAsync(CancellationToken ct)
    {
        if (IsEnsured) return;
        try
        {
            await _ensured.Task.WaitAsync(_consumerWaitLimit, ct);
        }
        catch (TimeoutException ex)
        {
            throw new QdrantBootstrapPendingException(_consumerWaitLimit, ex);
        }
    }
}

/// <summary>Qdrant のコレクション・索引の保証がまだ成功していないので、受け口が処理を始められない（再試行で吸収する）。</summary>
public sealed class QdrantBootstrapPendingException(TimeSpan waited, Exception inner)
    : Exception($"Qdrant のコレクション・索引の保証がまだ成功していない（{waited.TotalSeconds:0} 秒待った）。再試行で受け直す", inner);
