using System.Diagnostics.Metrics;
using Platform.Shared.Contracts.Dtos;

namespace Platform.Shared.Infrastructure.Foundation.Introspection;

// FR-15, ADR-0018, NFR-21, [[IADR-0652]] (#2143): 構成ドリフトの検出結果を計器にする（警報 ConfigDriftDetected の元）。
//
// ゲージ `config.drift.findings`（Prometheus では `config_drift_findings`）= **直近の検出で見つかった不一致の件数**。
// 0 が正常。ドリフトは「状態」なので計数ではなくゲージにする —— 直すまで鳴り続け、直せば収まる（計数の前進は窓が過ぎると収まってしまう）。
// 検出が 1 度も走っていない間は系列を出さない（0 と「まだ見ていない」を混ぜない）。値は検出のたびに差し替わる（定期と PostSync の即時の両方。
// `DriftRunner` が単一の経路）。検出が失敗した回は値を変えない（直前の結果のまま）。
public sealed class ConfigDriftMetrics
{
    public const string MeterName = "microservices-platform.config-inspection";
    public const string FindingsGaugeName = "config.drift.findings";

    // 直近の件数。-1 は「まだ検出していない」（観測しない）。
    private long _findings = -1;

    public ConfigDriftMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge(FindingsGaugeName, Observe, unit: "{finding}",
            description: "直近の構成ドリフト検出で見つかった不一致の件数（0 が正常。検出の前は系列なし）");
    }

    // 判定は HasDrift が正（`DriftRunner` と同じ）。HasDrift なのに findings が空なら 1 と数える（0 にして警報を黙らせない）。
    public void Record(DriftReportDto report) =>
        Interlocked.Exchange(ref _findings, report.HasDrift ? Math.Max(1, report.Findings.Count) : 0);

    private IEnumerable<Measurement<long>> Observe()
    {
        var value = Interlocked.Read(ref _findings);
        if (value >= 0) yield return new Measurement<long>(value);
    }
}
