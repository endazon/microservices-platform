using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Tests.Testing;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Introspection;

// FR-15, ADR-0018, IADR-0029 フォローアップ 4 (#901): ドリフト検出の**運搬経路**を固定する。
//
// 🔴 **本ファイルが塞ぐ穴。** DriftRunner / DriftDetectionHostedService を参照するテストは
// リポジトリ全体に 1 件も無かった（型名・メソッド名の双方で走査した。issue #901 の先行走査の結論と一致）。
// LoggingDriftAlertSink も、Bff 側がダブルを差し込むだけで**実装は 1 行も通っていない**。
//
// この経路が壊れても **起動は成功し、要求も普通に返る**。壊れ方が静かなのが問題であり、
// 「ドリフトを検出して警告する」（FR-15）が効かなくなったことに気づく手段が無い。
public class DriftDetectionChainTests
{
    private static DriftReportDto Report(bool hasDrift, params DriftFindingDto[] findings) =>
        new(hasDrift, DateTimeOffset.UnixEpoch, findings);

    private static DriftFindingDto Finding(string target = "convert") =>
        new(DriftDetector.Unverifiable, DriftDetector.SeverityWarning, target, "detail for " + target);

    // 呼び出し回数と受け取った report を記録する検出サービスのダブル。
    private sealed class StubInspection(Func<DriftReportDto> next) : IConfigInspectionService
    {
        public int Calls { get; private set; }

        public Task<DriftReportDto> GetDriftAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(next());
        }

        public Task<EffectiveConfigDto> GetEffectiveConfigAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("本試験では使わない");

        public Task<IReadOnlyList<ConfigVersionEntryDto>> GetVersionHistoryAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("本試験では使わない");
    }

    private sealed class RecordingSink : IDriftAlertSink
    {
        public List<DriftReportDto> Alerts { get; } = [];

        public Task AlertAsync(DriftReportDto report, CancellationToken ct = default)
        {
            Alerts.Add(report);
            return Task.CompletedTask;
        }
    }

    // ── DriftRunner ──────────────────────────────────────────────────────────

    // FR-15: 不一致があれば **その report のまま** 警告先へ渡す。
    [Fact]
    public async Task 不一致があれば検出結果をそのまま警告先へ渡す()
    {
        var report = Report(true, Finding());
        var sink = new RecordingSink();
        var runner = new DriftRunner(
            new StubInspection(() => report), sink, new RecordingLogger<DriftRunner>());

        var returned = await runner.RunOnceAsync(TestContext.Current.CancellationToken);

        sink.Alerts.Should().ContainSingle().Which.Should().BeSameAs(report,
            "警告先へ渡す report を作り直すと、検出結果と警告内容が食い違う");
        returned.Should().BeSameAs(report, "呼び出し元（PostSync フック）も同じ結果を受け取る");
    }

    // 🔴 FR-15: **不一致が無ければ警告しない**（上の試験の対照条件）。
    // これが無いと「常に警告する」実装でも上が通り、運用アラートが常時鳴って無視されるようになる
    // ——「検出して警告する」が実質的に死ぬのはこの壊れ方である。
    [Fact]
    public async Task 不一致が無ければ警告先を呼ばない()
    {
        var sink = new RecordingSink();
        var runner = new DriftRunner(
            new StubInspection(() => Report(false)), sink, new RecordingLogger<DriftRunner>());

        var returned = await runner.RunOnceAsync(TestContext.Current.CancellationToken);

        sink.Alerts.Should().BeEmpty();
        returned.HasDrift.Should().BeFalse();
    }

    // FR-15: HasDrift=true なら findings が空でも警告する（判定は HasDrift が正）。
    [Fact]
    public async Task 判定はHasDriftが正でありfindingsの件数では決めない()
    {
        var sink = new RecordingSink();
        var runner = new DriftRunner(
            new StubInspection(() => Report(true)), sink, new RecordingLogger<DriftRunner>());

        await runner.RunOnceAsync(TestContext.Current.CancellationToken);

        sink.Alerts.Should().ContainSingle();
    }

    // ── LoggingDriftAlertSink ────────────────────────────────────────────────

    // FR-15: finding 1 件につき Warning 1 件を出し、**運用アラートの抽出キー ConfigDrift=true**
    // を構造化フィールドとして含める。
    // 🔴 このキーが落ちると 05_observability-ops のアラート抽出が一致しなくなり、
    // ログには出ているのに**誰にも通知されない**状態になる（最も静かな壊れ方）。
    [Fact]
    public void 警告先はfindingごとにConfigDriftキー付きのWarningを出す()
    {
        var logger = new RecordingLogger<LoggingDriftAlertSink>();
        var sink = new LoggingDriftAlertSink(logger);

        sink.AlertAsync(Report(true, Finding("convert"), Finding("index")), TestContext.Current.CancellationToken)
            .IsCompletedSuccessfully.Should().BeTrue();

        var warnings = logger.OfLevel(LogLevel.Warning);
        warnings.Should().HaveCount(2, "finding 1 件につき 1 件の警告を出す");
        warnings.Select(w => w.State.Single(kv => kv.Key == "DriftTarget").Value)
            .Should().BeEquivalentTo(["convert", "index"]);
        warnings.Should().AllSatisfy(w => w.State.Should().Contain(
            kv => kv.Key == "ConfigDrift" && Equals(kv.Value, true),
            "ConfigDrift=true は運用アラートの抽出キーである（05_observability-ops）"));
    }

    // FR-15: finding が無ければ 1 件も出さない（上の対照条件）。
    [Fact]
    public void 警告先はfindingが無ければログを出さない()
    {
        var logger = new RecordingLogger<LoggingDriftAlertSink>();

        new LoggingDriftAlertSink(logger)
            .AlertAsync(Report(true), TestContext.Current.CancellationToken);

        logger.Entries.Should().BeEmpty();
    }

    // ── DriftDetectionHostedService ──────────────────────────────────────────
    //
    // PeriodicTimer の間隔は下限 10 秒（Math.Max(10, …)）のため、**2 周目を待つ試験は書かない**。
    // 初回実行・例外耐性・停止の 3 点で固定する。

    // 記録用 runner。所定回数まで例外を投げてから正常応答へ切り替えられる。
    private sealed class SignallingRunner(int throwFirstN = 0) : IDriftRunner
    {
        private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task FirstCall => _firstCall.Task;

        public Task<DriftReportDto> RunOnceAsync(CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _calls);
            _firstCall.TrySetResult();
            if (n <= throwFirstN)
                throw new InvalidOperationException("検出に失敗した");
            return Task.FromResult(Report(false));
        }
    }

    private static DriftDetectionHostedService Service(IDriftRunner runner, DriftDetectionOptions options) =>
        new(runner, Options.Create(options),
            new RecordingLogger<DriftDetectionHostedService>());

    // FR-15: 無効化されていれば検出を **一度も** 走らせずに終了する。
    // 🔴 テスト・ローカルで雑音を避けるための設定であり、効かないと全テストが
    // 背後で定期 HTTP 収集を始める（Docker 無しの環境では到達不能ログの雑音源になる）。
    [Fact]
    public async Task 無効化されていれば検出を一度も走らせずに終了する()
    {
        var runner = new SignallingRunner();
        var service = Service(runner, new DriftDetectionOptions { Enabled = false });

        await service.StartAsync(TestContext.Current.CancellationToken);

        // 無効時は ExecuteAsync が即座に返る（ループへ入らない）。
        // 🔴 **待ち時間を必ず区切る。** 素の `await ExecuteTask` にすると、無効化が効かなくなる
        // 退行のときテストは失敗ではなく**ハングする**（次のティックまで 300 秒待つため）。
        // ハングは CI ではタイムアウトとして現れ、どのアサーションが壊れたか判らない。
        service.ExecuteTask.Should().NotBeNull();
        var completed = async () => await service.ExecuteTask!.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await completed.Should().NotThrowAsync(
            "無効化されていればループへ入らず ExecuteAsync は即座に返る");
        runner.Calls.Should().Be(0);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    // FR-15 (#146): 有効なら **最初のティックを待たずに 1 回検出する**（do-while の初回）。
    // 宣言の適用直後はロールアウトが起きるため、起動時検出が適用直後検出を兼ねている。
    // 待ってから走る実装に退行すると、適用直後のドリフトが最大 5 分見逃される。
    [Fact]
    public async Task 有効なら最初のティックを待たずに一回検出する()
    {
        var runner = new SignallingRunner();
        var service = Service(runner, new DriftDetectionOptions { Enabled = true, IntervalSeconds = 300 });

        await service.StartAsync(TestContext.Current.CancellationToken);
        await runner.FirstCall.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        runner.Calls.Should().Be(1, "間隔 300 秒を待たずに初回が走る");

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    // 🔴 FR-15: **1 回の検出失敗でループを殺さない。**
    // 例外が ExecuteAsync まで抜けると BackgroundService は静かに停止し、以後ドリフト検出は
    // 二度と走らない（既定ホストでは 1 回の失敗でプロセスが落ちるか、黙って止まる）。
    // 「検出が止まったこと」自体は何のアラートも出さないので、最も静かな壊れ方である。
    [Fact]
    public async Task 検出が例外で失敗してもループは死なない()
    {
        var runner = new SignallingRunner(throwFirstN: 1);
        var service = Service(runner, new DriftDetectionOptions { Enabled = true, IntervalSeconds = 300 });

        await service.StartAsync(TestContext.Current.CancellationToken);
        await runner.FirstCall.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // 初回は例外。ループは次のティック待ちへ入っているはずで、ExecuteTask は生きている。
        service.ExecuteTask!.IsCompleted.Should().BeFalse(
            "検出の失敗が ExecuteAsync を抜けると、以後ドリフト検出は二度と走らない");
        service.ExecuteTask.IsFaulted.Should().BeFalse();

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    // FR-15: 停止要求では例外を外へ出さずに終了する（OperationCanceledException を吸収する）。
    // 落ちると、正常なシャットダウンのたびにホストがエラーを記録する。
    [Fact]
    public async Task 停止要求では例外を外へ出さずに終了する()
    {
        var runner = new SignallingRunner();
        var service = Service(runner, new DriftDetectionOptions { Enabled = true, IntervalSeconds = 300 });

        await service.StartAsync(TestContext.Current.CancellationToken);
        await runner.FirstCall.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await service.StopAsync(TestContext.Current.CancellationToken);

        // 🔴 **StopAsync の戻りだけを見ても検出できない。** BackgroundService.StopAsync は
        // ExecuteTask の完了を Task.WhenAny で待つだけで、**その例外を観測しない** ——
        // ループが例外で落ちても StopAsync は正常に返る（変異試験 M-11 で実測した）。
        // 落ちたことは ExecuteTask を直接 await して初めて表に出る。
        var drain = async () => await service.ExecuteTask!.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await drain.Should().NotThrowAsync(
            "正常なシャットダウンのたびにホストがエラーを記録することになる");
        service.ExecuteTask!.IsFaulted.Should().BeFalse();
    }
}
