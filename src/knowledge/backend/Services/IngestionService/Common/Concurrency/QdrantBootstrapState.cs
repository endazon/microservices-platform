namespace IngestionService.Common.Concurrency;

// FR-02, FR-03, [[IADR-0650]] (#2283): Qdrant のコレクション・索引の保証（`QdrantBootstrapHostedService`）が**一度でも成功したか**。
// readiness（`QdrantBootstrapHealthCheck`）と、コレクションを前提にする後続の器（2-gram の後付け・属性キーの索引の発見・
// 埋め込み待ちの埋め直し）が読む。保証は失敗すると再試行するので、**登録順だけでは前後が決まらない**ためにこれを待つ。
// プロセスで 1 つ（singleton）。一度立てたら戻さない（保証は冪等で、作ったコレクションは消えない前提）。
public sealed class QdrantBootstrapState
{
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
}
