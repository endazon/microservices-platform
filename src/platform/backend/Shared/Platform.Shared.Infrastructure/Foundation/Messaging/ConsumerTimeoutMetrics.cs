using System.Diagnostics.Metrics;

namespace Platform.Shared.Infrastructure.Foundation.Messaging;

// FR-02, FR-13, FR-17, ADR-0006, ADR-0027 (#1640): 受け口の外への呼び出しが**時間切れ**で終わった回数。
//
// 取り消し（停止要求・受け口の実行期限）はここへ数えない —— 数えるのは `ConsumerCallTimeouts` が
// 「自分の期限が立ち、呼び出し元の ct は立っていない」と判定したときだけである。
//
// タグの基数は閉じている: 段名（pipeline.json の `steps[].name`）と、受け口がコードで書いた呼び出し先の名前
// （`content`・`embedding`・`vector-store` 等の定数）だけで、入力（文書 ID・URI）は載せない。
public sealed class ConsumerTimeoutMetrics
{
    // 共通の Meter。`AddPlatformObservability` が OTLP の収集対象へ入れる（サービスごとの AddMeter は要らない）。
    public const string MeterName = "microservices-platform.messaging";

    // Prometheus 側では `messaging_consumer_timeout_total` になる（`.` → `_`）。
    public const string TimeoutCounterName = "messaging.consumer.timeout";

    public const string StepTag = "messaging.step";
    public const string TargetTag = "messaging.timeout.target";

    private readonly Counter<long> _timeouts;

    public ConsumerTimeoutMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _timeouts = meter.CreateCounter<long>(
            TimeoutCounterName,
            unit: "{timeout}",
            description: "受け口の外への呼び出しが時間切れで終わった回数（取り消しは数えない）");
    }

    public void RecordTimeout(string step, string target) =>
        _timeouts.Add(1,
            new KeyValuePair<string, object?>(StepTag, step),
            new KeyValuePair<string, object?>(TargetTag, target));
}
