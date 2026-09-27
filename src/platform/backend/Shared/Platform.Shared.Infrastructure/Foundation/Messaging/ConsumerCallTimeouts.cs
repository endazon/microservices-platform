using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Foundation.Messaging;

// FR-02, FR-13, FR-17, ADR-0027, ADR-0029 (#1640): Wolverine の受け口から外へ出る 1 回の呼び出しに、
// 受け口の実行期限より十分短い**呼び出しごとの期限**を付け、その時間切れを「取り消し」ではなく**時間切れとして**表す。
//
// ## なぜ要るか
//
// Wolverine は受け口へ渡す ct に 1 通ごとの実行期限（`HandlerChain.ExecutionTimeoutInSeconds`、未設定なら
// `WolverineOptions.DefaultExecutionTimeout`＝既定 60 秒）を連結する。外への呼び出しが `HttpClient` の既定 100 秒や
// 期限なしの gRPC に頼ると、止まった依存先は**いつも受け口の ct が先に立つ形で**終わる —— ログにも計器にも
// 「取り消し」としか残らず、依存先の時間切れと停止要求の区別が付かない（#1621 の監査で実測した型）。
//
// ## 形
//
// - 期限は ct の連結で与える（`timeout` 経過で立つ CTS と呼び出し元の ct の連結）。HTTP・gRPC・Qdrant・S3 の
//   どの輸送も ct を尊重するので、**輸送ごとの設定（`HttpClient.Timeout`・gRPC の `Deadline`）へ書き分けない**。
//   単例のクライアント（Qdrant・gRPC 生成クライアント）の既定を変えると、同じクライアントを使う検索や
//   起動時処理の期限まで変わるため、期限は**受け口の呼び出しの側**に置く。
// - 🔴 **捕捉は「自分の期限が立ち、呼び出し元の ct は立っていない」ときだけ**（`!ct.IsCancellationRequested`。
//   #1604 / #1621 と同じ絞り）。呼び出し元の取り消し（停止要求・受け口の実行期限）は**畳まずにそのまま外へ出す**。
// - 期限が立った後に呼び出しが投げたものは、型を問わず時間切れの結果として扱う（`OperationCanceledException`・
//   `RpcException(Cancelled)`・ライブラリの包み例外のどれでも）。自分の期限が立っていなければ何も変えない。
// - 時間切れは `ConsumerTimeoutException`（`TimeoutException` の派生）で投げ直し、警告ログと計器
//   （`ConsumerTimeoutMetrics`）に残す。受け口はそれを投げたまま再試行・デッドレター（`UsePlatformMessagingDefaults`）へ委ねる。
public sealed class ConsumerCallTimeouts(ConsumerTimeoutMetrics metrics, ILogger<ConsumerCallTimeouts> logger)
{
    public async Task<T> RunAsync<T>(
        string step, string target, TimeSpan timeout,
        Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        try
        {
            return await call(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (timer.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw TimedOut(step, target, timeout, ex);
        }
    }

    public Task RunAsync(
        string step, string target, TimeSpan timeout,
        Func<CancellationToken, Task> call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        return RunAsync(step, target, timeout, async t =>
        {
            await call(t).ConfigureAwait(false);
            return true;
        }, ct);
    }

    // 受け口が自分で持つ総枠（呼び出しの回数が入力で変わるとき）を使い切ったことを、呼び出しごとの期限と
    // 同じ計器・同じ例外型で表す。記録してから例外を返す（投げるのは呼び出し側）。
    public ConsumerTimeoutException BudgetExhausted(string step, string target, TimeSpan budget, string detail)
    {
        metrics.RecordTimeout(step, target);
        logger.LogWarning(
            "Consumer step {Step} exhausted its {Target} budget of {BudgetSeconds} s ({Detail}); "
            + "treating it as a timeout and leaving the message to retry / dead-letter",
            step, target, budget.TotalSeconds, detail);
        return new ConsumerTimeoutException(step, target, budget,
            $"Consumer step '{step}' exhausted its '{target}' budget of {budget.TotalSeconds:0.###} s ({detail})");
    }

    private ConsumerTimeoutException TimedOut(string step, string target, TimeSpan timeout, Exception inner)
    {
        metrics.RecordTimeout(step, target);
        logger.LogWarning(inner,
            "Consumer step {Step}: call to {Target} did not complete within {TimeoutSeconds} s; "
            + "treating it as a timeout (not a cancellation) and leaving the message to retry / dead-letter",
            step, target, timeout.TotalSeconds);
        return new ConsumerTimeoutException(step, target, timeout,
            $"Consumer step '{step}': call to '{target}' did not complete within {timeout.TotalSeconds:0.###} s",
            inner);
    }
}

// 受け口の外への呼び出しの時間切れ（または受け口が持つ総枠の使い切り）。`TimeoutException` の派生なので、
// 取り消し（`OperationCanceledException`）と型で区別できる。
public sealed class ConsumerTimeoutException(
    string step, string target, TimeSpan limit, string message, Exception? inner = null)
    : TimeoutException(message, inner)
{
    public string Step { get; } = step;
    public string Target { get; } = target;
    public TimeSpan Limit { get; } = limit;
}
