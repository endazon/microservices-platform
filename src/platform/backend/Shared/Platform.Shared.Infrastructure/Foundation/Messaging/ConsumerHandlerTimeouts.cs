using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Platform.Shared.Infrastructure.Foundation.Messaging;

// FR-02, FR-13, FR-17, ADR-0027 (#1640): 受け口の実行期限と、その内側に収める時間の予算の組み立て。
public static class ConsumerHandlerTimeouts
{
    // Wolverine の `WolverineOptions.DefaultExecutionTimeout` の既定（WolverineFx 6.24.4 で 60 秒）。
    // 方針（`HandlerExecutionTimeoutPolicy<T>`）を入れない受け口の予算はこの値に対して検査する。
    // 値は `ConsumerHandlerTimeoutsTests` が Wolverine の実物と突き合わせて固定する（版更新で変わったら見直す）。
    public static readonly TimeSpan WolverineDefault = TimeSpan.FromSeconds(60);

    // 構成から秒数を読む。1 未満は 1 に丸める（#1604 の `Mcp:DeclarationTimeoutSeconds`・#1621 と同じ扱い）。
    public static TimeSpan Seconds(IConfiguration configuration, string key, int defaultSeconds)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue<int?>(key) ?? defaultSeconds));
    }

    // 🔴 起動時の検査: 受け口の最悪の所要時間（呼び出しごとの期限 × 回数の和）が受け口の実行期限に収まらなければ、
    // 起動を止める。収まらない構成では、止まった依存先は呼び出しごとの期限より先に受け口の ct で切られ、
    // 時間切れではなく取り消しとして記録される —— それを実行時の失敗で初めて知ることにしない。
    // 等しいときも止める（受け口の ct と同着では、どちらが先に立つかが定まらない）。
    public static void EnsureFits(
        string step, TimeSpan handlerTimeout, string handlerTimeoutSource,
        params (string Part, TimeSpan Worst)[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var total = parts.Aggregate(TimeSpan.Zero, (sum, p) => sum + p.Worst);
        if (handlerTimeout > total)
            return;

        var breakdown = string.Join(" ＋ ", parts.Select(p => $"{p.Part}（{p.Worst.TotalSeconds} 秒）"));
        throw new InvalidOperationException(
            $"受け口 {step} の実行期限（{handlerTimeoutSource}＝{handlerTimeout.TotalSeconds} 秒）は、"
            + $"外への呼び出しの最悪の所要時間 {total.TotalSeconds} 秒（{breakdown}）より長くなければならない。"
            + " 収まらないと、止まった依存先が時間切れではなく受け口の取り消しとして記録される。");
    }

    // ブローカ（RabbitMQ）の `consumer_timeout` として仮定する値の構成キーと既定（RabbitMQ 3.13 の既定 30 分。配備は上書きしていない）。
    // 配備で `consumer_timeout` を変えたら、この値も合わせる。
    public const string BrokerConsumerTimeoutKey = "Messaging:BrokerConsumerTimeoutSeconds";
    public const int DefaultBrokerConsumerTimeoutSeconds = 1800;

    public static TimeSpan BrokerConsumerTimeout(IConfiguration configuration) =>
        Seconds(configuration, BrokerConsumerTimeoutKey, DefaultBrokerConsumerTimeoutSeconds);

    // 🔴 起動時の検査（その 2）: **1 回の配信の再試行の連鎖全体**がブローカの `consumer_timeout` に収まること。
    //
    // WolverineFx.RabbitMQ 6.24.4 の受信は Inline で、再試行（`RetryInlineContinuation`）は**同じ配信の中で**回り、
    // ack は最後の試行の後にしか返らない。したがって 1 通が配信を握る時間の上限は
    // 「試行上限 × 受け口の実行期限 ＋ 試行間の待ちの合計」である。これが `consumer_timeout` 以上だと、ブローカはチャネルを閉じて
    // 再配信し、試行回数が 0 に戻る —— 失敗し続ける 1 通が永久に回り、その間キューを塞ぐ。等しいときも止める。
    public static void EnsureRetryChainFits(
        string step, TimeSpan handlerTimeout, string handlerTimeoutSource, int maxAttempts,
        TimeSpan totalRetryCooldown, TimeSpan brokerConsumerTimeout)
    {
        var chain = handlerTimeout * maxAttempts + totalRetryCooldown;
        if (chain < brokerConsumerTimeout)
            return;

        throw new InvalidOperationException(
            $"受け口 {step} の 1 回の配信の再試行の連鎖（試行 {maxAttempts} 回 × {handlerTimeoutSource}＝{handlerTimeout.TotalSeconds} 秒"
            + $" ＋ 試行間の待ち {totalRetryCooldown.TotalSeconds} 秒 ＝ {chain.TotalSeconds} 秒）は、"
            + $"ブローカの consumer_timeout（{BrokerConsumerTimeoutKey}＝{brokerConsumerTimeout.TotalSeconds} 秒）より短くなければならない。"
            + " 超えるとブローカが配信を取り上げて再配信し、試行回数が戻って失敗し続ける 1 通が永久に回る。");
    }

    // 共通の時間切れの判定・計器を DI へ登録する（受け口がコンストラクタで受ける）。
    public static IServiceCollection AddPlatformConsumerTimeouts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMetrics();
        services.TryAddSingleton<ConsumerTimeoutMetrics>();
        services.TryAddSingleton<ConsumerCallTimeouts>();
        return services;
    }
}

// FR-02, ADR-0027 (#1640): 1 つのメッセージ型の受け口にだけ実行期限を与える（他の型の既定は変えない）。
// Wolverine は 1 通ごとに `HandlerChain.ExecutionTimeoutInSeconds`（未設定なら `DefaultExecutionTimeout`）の CTS を張り、
// 停止要求と連結した ct を受け口へ渡す。受け口の最悪の所要時間が既定の 60 秒を正当に超えるときに使う
// （ConversionService の `RawDocumentFetchedTimeoutPolicy`〔#1621〕を型引数にした形）。
public sealed class HandlerExecutionTimeoutPolicy<TMessage>(TimeSpan handlerTimeout) : IHandlerPolicy
{
    public int TimeoutSeconds { get; } = (int)Math.Ceiling(handlerTimeout.TotalSeconds);

    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(c => c.MessageType == typeof(TMessage)))
            chain.ExecutionTimeoutInSeconds = TimeoutSeconds;
    }
}
