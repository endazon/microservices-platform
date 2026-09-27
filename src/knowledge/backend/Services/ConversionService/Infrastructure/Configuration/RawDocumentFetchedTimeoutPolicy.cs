using JasperFx;
using JasperFx.CodeGeneration;
using Knowledge.Contracts.Events;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace ConversionService.Infrastructure.Configuration;

// FR-12, UC-06, IADR-0008（2026-09-27 追記 / #1621）: 変換の受け口（`RawDocumentFetched` のハンドラ）の実行期限を明示する。
//
// Wolverine は 1 通ごとに `HandlerChain.ExecutionTimeoutInSeconds`（未設定なら `WolverineOptions.DefaultExecutionTimeout`・
// 既定 60 秒）の CTS を張り、停止要求と連結した ct を受け口へ渡す。この期限は本文変換（pandoc）・図のコード化・保管の
// **すべて**に掛かるので、図のコード化の総枠と 1 回の期限（`DiagramCodingLimits`）が収まる長さをここで与える。
// 他のメッセージ型の既定は変えない。
public sealed class RawDocumentFetchedTimeoutPolicy(TimeSpan handlerTimeout) : IHandlerPolicy
{
    public int TimeoutSeconds { get; } = (int)Math.Ceiling(handlerTimeout.TotalSeconds);

    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(c => c.MessageType == typeof(RawDocumentFetched)))
            chain.ExecutionTimeoutInSeconds = TimeoutSeconds;
    }
}
