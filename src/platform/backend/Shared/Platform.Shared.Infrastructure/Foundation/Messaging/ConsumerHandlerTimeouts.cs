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
