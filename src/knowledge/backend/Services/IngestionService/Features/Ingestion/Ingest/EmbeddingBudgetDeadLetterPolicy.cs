using JasperFx;
using JasperFx.CodeGeneration;
using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace IngestionService.Features.Ingestion.Ingest;

// FR-02, UC-04, ADR-0027 (#1640): **埋め込みの総枠を使い切った文書は、再試行せずにデッドレターへ送る。**
//
// 総枠の使い切りは文書の大きさ（チャンク数）で決まり、何度試しても同じ結果になる。再試行（`UsePlatformMessagingDefaults` の
// 4 試行・2/10/30 秒）へ流すと、1 回の配信が「4 × 受け口の実行期限 ＋ 42 秒」配信を握り続け、そのたびに総枠分の埋め込みを
// 使い直す。Wolverine の RabbitMQ の受信は Inline で、再試行は同じ配信の中で回るため、キューも同じ時間だけ塞がる。
//
// 🔴 **呼び出しごとの時間切れ（`content` / `embedding` / `vector-store`）は従来どおり再試行する** —— 止まった依存先は
// 一時的に回復し得る。デッドレターへ直行させるのは呼び出し先が `embedding-budget` の `ConsumerTimeoutException` だけである。
// 受け口（チェーン）に付けた規則は、全体の既定（`OnAnyException().RetryWithCooldown`）より先に評価される
// （`EmbeddingBudgetDeadLetterPipelineTests` がローカルキューの実配送で確かめる）。
public sealed class EmbeddingBudgetDeadLetterPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(c => c.MessageType == typeof(DocumentUpdated)))
            chain.OnException<ConsumerTimeoutException>(IsBudgetExhausted).MoveToErrorQueue();
    }

    internal static bool IsBudgetExhausted(ConsumerTimeoutException ex) =>
        ex.Target == IngestionTimeouts.EmbeddingBudgetTarget;
}
