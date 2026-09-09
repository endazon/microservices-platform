using System.Diagnostics.Metrics;
using System.Security.Claims;
using AwesomeAssertions;
using DocumentService.Common.Observability;

namespace DocumentService.Tests.Features.Documents;

// FR-05, FR-09, FR-16, SC-10, SC-12, ADR-0036, ADR-0062, ADR-0085 決定 4, [[IADR-0420]] (#1233):
// **ユニットの主体が保存した文書のうち `project` を持たない件数**（0 が正常）。
//
// ここで固定するのは**母集合の 2 条件**である —— ①主体が無人であること
// ②保存後の属性に `project` が無いこと。**どちらか一方だけで数える実装は陽性対照で落ちる。**
[Trait("TestKind", "Unit")]
public sealed class UnitProjectMetricsTests
{
    private const string KbWriter = "ai-stock-trading-kb-writer";

    private static ClaimsPrincipal Machine()
        => new(new ClaimsIdentity(
            [new Claim("preferred_username", $"service-account-{KbWriter}"), new Claim("azp", KbWriter)],
            "Test"));

    // 🔴 対話ログインの人間。**`azp` を持っている**（SPA の clientId）。
    private static ClaimsPrincipal Human()
        => new(new ClaimsIdentity(
            [new Claim("preferred_username", "hanako"), new Claim("azp", "platform-spa")], "Test"));

    private static (UnitProjectMetrics Metrics, MetricsProbe Probe) Build()
    {
        var factory = new ScopedMeterFactory();
        var metrics = new UnitProjectMetrics(factory);
        return (metrics, new MetricsProbe(factory));
    }

    private static Dictionary<string, string> Attributes(string? project) =>
        project is null
            ? new Dictionary<string, string> { ["confidentiality"] = "internal" }
            : new Dictionary<string, string> { ["confidentiality"] = "internal", ["project"] = project };

    // (a) 無人主体 ＋ `project` 欠落 → 計上する。**これが規定違反の検出そのものである。**
    [Fact]
    public void 無人主体がprojectなしで保存したら計上する()
    {
        var (metrics, probe) = Build();

        metrics.RecordIfUnitSubjectSavedWithoutProject(
            Machine(), Attributes(null), UnitProjectMetrics.OperationCreate);

        probe.Total.Should().Be(1);
        probe.TagsOf(UnitProjectMetrics.ClientIdTag).Should().Equal(KbWriter);
        probe.TagsOf(UnitProjectMetrics.OperationTag).Should().Equal(UnitProjectMetrics.OperationCreate);
    }

    // (b) 陽性対照: 無人主体でも `project` が在れば計上しない（規定どおりの保存を数えない）。
    [Fact]
    public void 無人主体でもprojectが在れば計上しない()
    {
        var (metrics, probe) = Build();

        metrics.RecordIfUnitSubjectSavedWithoutProject(
            Machine(), Attributes("ai-stock-trading"), UnitProjectMetrics.OperationCreate);

        probe.Total.Should().Be(0, "ユニット側の保存経路は project を無条件で付与する（正常）");
    }

    // (c) 🔴 陽性対照: 対話ログインの人間は `project` が無くても計上しない。
    // **計画は基盤で `project` を任意と定めており（ADR-0085 決定 1）、人間の保存を数えると
    // 母数がほぼ全件で張り付いて指標が動かなくなる**（決定 4 が退けた数え方そのもの）。
    [Fact]
    public void 対話ログインの人間がprojectなしで保存しても計上しない()
    {
        var (metrics, probe) = Build();

        metrics.RecordIfUnitSubjectSavedWithoutProject(
            Human(), Attributes(null), UnitProjectMetrics.OperationCreate);

        probe.Total.Should().Be(0, "測るのは書き手の側であり、人間の保存は母集合に入らない");
    }

    // 未認証は計上しない（主体が決まらないものを無人主体と読まない）。
    [Fact]
    public void 未認証は計上しない()
    {
        var (metrics, probe) = Build();

        metrics.RecordIfUnitSubjectSavedWithoutProject(
            new ClaimsPrincipal(new ClaimsIdentity()), Attributes(null),
            UnitProjectMetrics.OperationCreate);

        probe.Total.Should().Be(0);
    }

    // 🔴 空文字の `project` は「持たない」と読む（`project=""` を送るだけで免れられない）。
    [Fact]
    public void 空のproject値は持たないものとして計上する()
    {
        var (metrics, probe) = Build();

        metrics.RecordIfUnitSubjectSavedWithoutProject(
            Machine(), Attributes("   "), UnitProjectMetrics.OperationUpdate);

        probe.Total.Should().Be(1);
        probe.TagsOf(UnitProjectMetrics.OperationTag).Should().Equal(UnitProjectMetrics.OperationUpdate);
    }

    // クライアント識別子を取り出せない無人主体は `unknown` へ倒す（属性の基数を閉じたまま計上する）。
    [Fact]
    public void クライアント識別子が無い無人主体はunknownで計上する()
    {
        var (metrics, probe) = Build();
        var noClientId = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("preferred_username", "service-account-")], "Test"));

        metrics.RecordIfUnitSubjectSavedWithoutProject(
            noClientId, Attributes(null), UnitProjectMetrics.OperationUpdateMetadata);

        probe.Total.Should().Be(1);
        probe.TagsOf(UnitProjectMetrics.ClientIdTag).Should().Equal(UnitProjectMetrics.UnknownClientId);
    }

    // **Meter の scope で購読を絞る**（`MeterListener` はプロセス全体を購読するため、
    // 名前で絞ると並行する他テストの測定が混ざる。`IngestTagFilterTests` は名前を一意にして
    // 同じ問題を避けているが、scope での照合はテスト名の付け方に依存しない）。
    public sealed class ScopedMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var m in _meters) m.Dispose();
            _meters.Clear();
        }
    }

    // `documents.unit_project_missing.total` の測定と属性を収集する。
    public sealed class MetricsProbe
    {
        private readonly List<(long Value, Dictionary<string, string?> Tags)> _measurements = [];

        public MetricsProbe(object scope)
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (ReferenceEquals(instrument.Meter.Scope, scope)
                        && instrument.Name == UnitProjectMetrics.MissingCounterName)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var copied = new Dictionary<string, string?>();
                foreach (var tag in tags) copied[tag.Key] = tag.Value?.ToString();
                lock (_measurements) _measurements.Add((value, copied));
            });
            listener.Start();
        }

        public long Total { get { lock (_measurements) return _measurements.Sum(m => m.Value); } }

        public IReadOnlyList<string?> TagsOf(string key)
        {
            lock (_measurements)
                return [.. _measurements.Select(m => m.Tags.GetValueOrDefault(key))];
        }

        public string? LastOperation => LastTag(UnitProjectMetrics.OperationTag);

        public string? LastClientId => LastTag(UnitProjectMetrics.ClientIdTag);

        private string? LastTag(string key)
        {
            lock (_measurements)
                return _measurements.Count == 0 ? null : _measurements[^1].Tags.GetValueOrDefault(key);
        }
    }
}
