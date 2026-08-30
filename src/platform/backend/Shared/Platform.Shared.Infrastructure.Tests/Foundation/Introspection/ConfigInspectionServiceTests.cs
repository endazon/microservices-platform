using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Introspection;

// FR-15, ADR-0018, IADR-0046 (#901): ConfigInspectionService の**実体**を固定する。
//
// 🔴 **本ファイルが塞ぐ穴。** 共有側の既存テスト（DriftDetectionChainTests）は
// `StubInspection : IConfigInspectionService` という**スタブ**を使っており、実体は 1 行も通らない。
// 型名・メソッド名の 2 軸でリポジトリ全体を走査すると、実体を new するのは
// Platform.Bff.Tests の ConfigVersionHistoryTests / ConfigVersionHistoryBindingTests だけで、
// しかも触るのは GetVersionHistoryAsync のみ。GetEffectiveConfigAsync / GetDriftAsync の
// 実体呼び出しは**本番の ConfigBffEndpoints.cs しか無い**（着手前の実測: line 61/101）。
//
// ここが壊れても **200 が返る。** 履歴の並びが逆になっても、縮退が誤っても、
// SC-11 が違うものを見せるだけで誰も落ちない —— 静かな壊れ方の典型である。
public class ConfigInspectionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubCollector(EffectiveCollection result) : IEffectiveConfigCollector
    {
        public Task<EffectiveCollection> CollectAsync(CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private static PipelineOptions DeclarationWithStep() => new()
    {
        Events = ["RawDocumentFetched", "DocumentNormalized"],
        Steps =
        [
            new PipelineStepOptions
            {
                Name = "convert",
                Service = "conversion-service",
                Consumer = "ConversionService.Features.ConversionJobs.RawDocumentFetchedConsumer",
                Input = "RawDocumentFetched",
                Outputs = ["DocumentNormalized"],
            },
        ],
    };

    private static ConfigInspectionService Build(
        PipelineOptions declaration,
        ConfigVersionOptions? version = null,
        EffectiveCollection? effective = null) =>
        new(new StubCollector(effective ?? EffectiveCollection.Empty),
            declaration,
            Options.Create(version ?? new ConfigVersionOptions()),
            new FixedTime());

    // ── GetDriftAsync ────────────────────────────────────────────────────────

    // 宣言が無ければ突合対象も無い＝ドリフト無し。下の試験の対照条件であり、
    // これが無いと「常に HasDrift=true」の実装でも通る。
    [Fact]
    public async Task 宣言が空ならドリフト無しを返す()
    {
        var report = await Build(new PipelineOptions())
            .GetDriftAsync(TestContext.Current.CancellationToken);

        report.HasDrift.Should().BeFalse();
        report.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task 不一致があれば_HasDrift_が真になる()
    {
        // 収集対象に登録が無い段＝恒久的に検証不能（DriftDetector が finding を出す）。
        var report = await Build(DeclarationWithStep())
            .GetDriftAsync(TestContext.Current.CancellationToken);

        report.Findings.Should().NotBeEmpty();
        // 🔴 HasDrift は findings.Count > 0 から導出される。定数化されると
        //    「検出はしているのに報告しない」形になり、警告経路が丸ごと死ぬ。
        report.HasDrift.Should().BeTrue();
    }

    [Fact]
    public async Task 検査時刻は_TimeProvider_から取る()
    {
        var report = await Build(DeclarationWithStep())
            .GetDriftAsync(TestContext.Current.CancellationToken);

        // DateTimeOffset.UtcNow を直に読む実装だと、時刻を固定した試験が書けなくなる。
        report.CheckedAt.Should().Be(Now);
    }

    // ── GetEffectiveConfigAsync ──────────────────────────────────────────────

    [Fact]
    public async Task 実効構成には構成バージョンが載る()
    {
        var version = new ConfigVersionOptions
        {
            GitCommit = "abc1234",
            AppliedAt = "2026-08-20T09:30:00Z",
            AppliedBy = "argocd",
        };

        var dto = await Build(DeclarationWithStep(), version)
            .GetEffectiveConfigAsync(TestContext.Current.CancellationToken);

        dto.Version.GitCommit.Should().Be("abc1234");
        dto.Version.AppliedBy.Should().Be("argocd");
        dto.Version.AppliedAt.Should().Be(new DateTimeOffset(2026, 8, 20, 9, 30, 0, TimeSpan.Zero));
    }

    // 適用日時が不正・空なら「不明（null）」へ縮退する。例外にすると、
    // GitOps の注入ミス 1 つで構成情報 API が丸ごと 500 になる。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-timestamp")]
    public async Task 適用日時が不正なら例外にせず不明にする(string? applied)
    {
        var version = new ConfigVersionOptions { GitCommit = "abc1234", AppliedAt = applied };

        var dto = await Build(DeclarationWithStep(), version)
            .GetEffectiveConfigAsync(TestContext.Current.CancellationToken);

        dto.Version.AppliedAt.Should().BeNull();
        dto.Version.GitCommit.Should().Be("abc1234", "日時が読めなくても他の項目は落とさない");
    }

    // ── GetVersionHistoryAsync ───────────────────────────────────────────────

    private static ConfigVersionOptions History(params (string Commit, string? Applied)[] entries) =>
        new()
        {
            History = [.. entries.Select(e => new ConfigVersionHistoryEntryOptions
            {
                GitCommit = e.Commit,
                AppliedAt = e.Applied,
                AppliedBy = "argocd",
            })],
        };

    // 🔴 SC-11 が「新しい順」で見せる根拠。逆順でも 200 が返るため、試験が無いと誰も気づかない。
    [Fact]
    public async Task 履歴は適用日時の降順に並べ替える()
    {
        var options = History(
            ("old", "2026-08-01T00:00:00Z"),
            ("new", "2026-08-20T00:00:00Z"),
            ("mid", "2026-08-10T00:00:00Z"));

        var history = await Build(new PipelineOptions(), options)
            .GetVersionHistoryAsync(TestContext.Current.CancellationToken);

        history.Select(h => h.GitCommit).Should().ContainInOrder("new", "mid", "old");
    }

    // 日時不明は末尾へ（DateTimeOffset.MinValue へ落とす実装の意図）。
    // 先頭へ来ると「最新の適用」を誤って見せる。
    [Fact]
    public async Task 適用日時が不明な履歴は末尾へ送る()
    {
        var options = History(
            ("unknown", null),
            ("dated", "2026-08-10T00:00:00Z"));

        var history = await Build(new PipelineOptions(), options)
            .GetVersionHistoryAsync(TestContext.Current.CancellationToken);

        history.Select(h => h.GitCommit).Should().ContainInOrder("dated", "unknown");
    }

    // OrderByDescending は安定ソートである。同一日時の並びは注入順（＝GitOps が並べた順）を保つ。
    [Fact]
    public async Task 同一日時の履歴は注入順を保つ()
    {
        var options = History(
            ("first", "2026-08-10T00:00:00Z"),
            ("second", "2026-08-10T00:00:00Z"));

        var history = await Build(new PipelineOptions(), options)
            .GetVersionHistoryAsync(TestContext.Current.CancellationToken);

        history.Select(h => h.GitCommit).Should().ContainInOrder("first", "second");
    }

    // 履歴未注入（dev/compose）の縮退: 現在バージョンを単一エントリで返す。
    // HadDrift は「その時点のドリフト有無は不明」なので null（false と偽らない）。
    [Fact]
    public async Task 履歴未注入なら現在バージョンの単一エントリへ縮退する()
    {
        var version = new ConfigVersionOptions
        {
            GitCommit = "current",
            AppliedAt = "2026-08-25T00:00:00Z",
            AppliedBy = "compose",
        };

        var history = await Build(new PipelineOptions(), version)
            .GetVersionHistoryAsync(TestContext.Current.CancellationToken);

        var only = history.Should().ContainSingle().Which;
        only.GitCommit.Should().Be("current");
        only.AppliedBy.Should().Be("compose");
        only.HadDrift.Should().BeNull("その時点のドリフト有無は不明であり false と偽らない");
    }

    // 上の対照条件。現在バージョンも空なら**空一覧**を返す。
    // ここで空エントリを 1 件作ると、SC-11 が「中身の無い適用履歴」を見せる。
    [Fact]
    public async Task 現在バージョンも空なら空一覧を返す()
    {
        var history = await Build(new PipelineOptions(), new ConfigVersionOptions())
            .GetVersionHistoryAsync(TestContext.Current.CancellationToken);

        history.Should().BeEmpty();
    }
}
