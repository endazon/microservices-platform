using System.Diagnostics.Metrics;

namespace LlmGateway.Common.Observability;

// NFR-18, NFR-21, IADR-0698: ゲートウェイのプロセスが起動した時刻（UNIX 秒）のゲージ。起動の回数はこの値の変わった回数で数える。
//
// クラウドの警報 LlmGatewayRestartLoop（deploy/cloud/observability/prometheus.yaml）が
// `changes(max(llm_gateway_start_time_seconds)[30m:1m])` で短い間の再起動の繰り返しを見る。
// 「1 ずつ増えるカウンタ」にしない理由: プロセスごとに 0 から数え直すので、再起動のたびに 1 へ戻る系列は
// 増えたことにならない（同じラベルなら increase が 0、プロセスごとに別のラベルなら系列の数え方が要る）。
// 起動の時刻はプロセスごとに必ず大きくなるので、ラベルが変わっても変わらなくても `max` の変わった回数が起動の回数になる。
//
// Prometheus 側の名前は `llm_gateway_start_time_seconds`（`{…}` の単位は注記で接尾辞にならない。IADR-0466 §Prometheus 側の名前と同じ変換）。
// 起動の直後に送り出す（Program.cs が ForceFlush する）。既定の 60 秒の送り出しを待つと、それより早く落ちるプロセスが数えられない。
public sealed class LlmGatewayStartMetrics
{
    public const string MeterName = LlmCompletionMetrics.MeterName;
    public const string StartTimeGaugeName = "llm.gateway.start_time_seconds";

    public LlmGatewayStartMetrics(IMeterFactory meterFactory, TimeProvider clock)
    {
        StartedAtUnixSeconds = clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge(
            StartTimeGaugeName, () => StartedAtUnixSeconds, unit: "{unix_second}",
            description: "LLM ゲートウェイのプロセスが起動した時刻（UNIX 秒）。値の変わった回数が起動の回数（短い間の再起動の繰り返しの警報に使う）。");
    }

    public double StartedAtUnixSeconds { get; }
}
