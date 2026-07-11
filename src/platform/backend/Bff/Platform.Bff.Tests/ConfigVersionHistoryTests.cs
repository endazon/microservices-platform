using FluentAssertions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Microsoft.Extensions.Options;

namespace Platform.Bff.Tests;

// FR-15 (#139), IADR-0046: 構成バージョン履歴のデータ源・縮退・並び順の単体テスト。
// 正データ源は GitOps 層（注入された ConfigVersionOptions.History）。API は永続化せず surfacing する。
public class ConfigVersionHistoryTests
{
    // 履歴は collector を参照しないため、呼ばれたら失敗する collector を渡して独立性を担保する。
    private sealed class UnusedCollector : IEffectiveConfigCollector
    {
        public Task<EffectiveCollection> CollectAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("履歴は自己申告収集に依存しない");
    }

    private static ConfigInspectionService Build(ConfigVersionOptions version) =>
        new(new UnusedCollector(), new PipelineOptions(),
            Options.Create(version), TimeProvider.System);

    // GitOps 注入の履歴を新しい順（AppliedAt 降順）に並べて返す。注入経路をそのまま surfacing する。
    [Fact]
    public async Task GetVersionHistory_WhenInjected_ReturnsNewestFirstWithDriftFlag()
    {
        var svc = Build(new ConfigVersionOptions
        {
            History =
            [
                new() { GitCommit = "old0000", AppliedAt = "2026-07-05T00:00:00Z", AppliedBy = "argocd", HadDrift = true },
                new() { GitCommit = "new1111", AppliedAt = "2026-07-08T00:00:00Z", AppliedBy = "gitops", HadDrift = false },
            ],
        });

        var history = await svc.GetVersionHistoryAsync();

        history.Should().HaveCount(2);
        history[0].GitCommit.Should().Be("new1111"); // 新しい順（降順）に整列
        history[0].HadDrift.Should().BeFalse();
        history[1].GitCommit.Should().Be("old0000");
        history[1].HadDrift.Should().BeTrue();
    }

    // 履歴未注入（dev/compose）時は現在バージョンの単一エントリへ縮退する。ドリフト有無は不明（null）。
    [Fact]
    public async Task GetVersionHistory_WhenNoHistoryButCurrentVersion_DegradesToCurrent()
    {
        var svc = Build(new ConfigVersionOptions
        {
            GitCommit = "cur9999",
            AppliedAt = "2026-07-07T00:00:00Z",
            AppliedBy = "argocd",
        });

        var history = await svc.GetVersionHistoryAsync();

        history.Should().ContainSingle();
        history[0].GitCommit.Should().Be("cur9999");
        history[0].AppliedBy.Should().Be("argocd");
        history[0].HadDrift.Should().BeNull();
    }

    // 履歴も現在バージョンも無ければ空一覧（dev の gitCommit 空と同じ縮退挙動）。
    [Fact]
    public async Task GetVersionHistory_WhenNothingKnown_ReturnsEmpty()
    {
        var svc = Build(new ConfigVersionOptions());

        var history = await svc.GetVersionHistoryAsync();

        history.Should().BeEmpty();
    }
}
