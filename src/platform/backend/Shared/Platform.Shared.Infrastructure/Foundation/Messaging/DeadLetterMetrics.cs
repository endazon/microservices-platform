using System.Diagnostics.Metrics;

namespace Platform.Shared.Infrastructure.Foundation.Messaging;

// NFR-21, ADR-0053 決定 1〜3, ADR-0012, IADR-0592 (#2043): **自動再試行を使い切ってデッドレターへ送った回数。**
//
// 契機は「再試行を使い切ったこと」だけである（ADR-0053 決定 2）。キュー名・ブローカの種類を契機にしない ——
// Wolverine の RabbitMQ は共有 1 本の `wolverine-dead-letter-queue` を使い（IADR-0234 決定 6）、キュー名の
// パターン照合では対象が 0 件になる（「無い」ではなく「見ていない」）。
//
// 上げる場所は共通既定 `WolverineExtensions.UsePlatformMessagingDefaults` の 1 箇所だけである（決定 3。
// 個別サービスで上げない）。受け口が独自の規則で再試行せずにデッドレターへ送る経路（取り込みの総枠の使い切り。
// `EmbeddingBudgetDeadLetterPolicy`）は**ここへ数えない** —— 再試行を使い切った失敗ではなく、
// 既に専用の計器（`messaging_consumer_timeout_total{messaging_timeout_target=~".*-budget"}`）と警報がある。
//
// タグの基数は閉じている: メッセージ型の短い名前（コードにある契約の型）と例外の型名だけで、
// 入力（文書 ID・URI・例外の文面）は載せない。サービス名は OTLP のリソース属性（Prometheus では `job`）が持つ。
public sealed class DeadLetterMetrics
{
    // 受け口の時間切れ（`ConsumerTimeoutMetrics`）と同じ Meter。`AddPlatformObservability` が収集対象へ入れている
    // （サービスごとの AddMeter は要らない）。
    public const string MeterName = ConsumerTimeoutMetrics.MeterName;

    // Prometheus 側では `messaging_dead_letter_total` になる（`.` → `_`、カウンタは `_total` が付く）。
    public const string DeadLetterCounterName = "messaging.dead_letter";

    public const string MessageTypeTag = "messaging.message.type";
    public const string ErrorTypeTag = "error.type";

    private readonly Counter<long> _deadLetters;

    public DeadLetterMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _deadLetters = meter.CreateCounter<long>(
            DeadLetterCounterName,
            unit: "{message}",
            description: "自動再試行を使い切ってデッドレターへ送ったメッセージの数（0 が正常）");
    }

    public void RecordRetryExhausted(string messageType, string errorType) =>
        _deadLetters.Add(1,
            new KeyValuePair<string, object?>(MessageTypeTag, messageType),
            new KeyValuePair<string, object?>(ErrorTypeTag, errorType));
}
