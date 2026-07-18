using FluentAssertions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Platform.Bff.Tests;

// FR-15 (#192), IADR-0046, IADR-0069: GitOps 注入配線の契約テスト。
// Helm が BFF へ描画する env `Config__History__<i>__<Prop>` は、ASP.NET Core の構成配列規約で
// 構成パス `Config:History:<i>:<Prop>`（環境変数の `__` は `:` に正規化）へ写る。ここでは同キーを
// バインドし、ConfigVersionOptions.History→ConfigInspectionService が実値の複数エントリ履歴を返すこと
// （縮退ではない）を検証して、Helm 側の env 命名（deployment.yaml）と Options 型の対応を固定する。
public class ConfigVersionHistoryBindingTests
{
    private sealed class UnusedCollector : IEffectiveConfigCollector
    {
        public Task<EffectiveCollection> CollectAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("履歴は自己申告収集に依存しない");
    }

    // ConfigInspectionExtensions と同じ経路（Configure<ConfigVersionOptions>(GetSection("Config"))）で
    // 構成キーを Options へバインドし、ConfigInspectionService を組み立てる。
    private static ConfigInspectionService BuildFromConfig(IDictionary<string, string?> configData)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.Configure<ConfigVersionOptions>(
            configuration.GetSection(ConfigVersionOptions.SectionName));
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ConfigVersionOptions>>();

        return new ConfigInspectionService(
            new UnusedCollector(), new PipelineOptions(), options, TimeProvider.System);
    }

    // Helm が複数エントリを注入した env 契約（Config__History__<i>__<Prop>）が、実値の履歴を返すこと。
    [Fact]
    public async Task InjectedHistoryEnvKeys_BindToMultiEntryHistory()
    {
        // env: Config__History__0__GitCommit 等 ＝ 構成パス Config:History:0:*（`__`→`:`）。
        var svc = BuildFromConfig(new Dictionary<string, string?>
        {
            ["Config:GitCommit"] = "cur9999",
            ["Config:History:0:GitCommit"] = "aaa1111",
            ["Config:History:0:AppliedAt"] = "2026-07-08T00:00:00Z",
            ["Config:History:0:AppliedBy"] = "argocd",
            ["Config:History:0:HadDrift"] = "false",
            ["Config:History:1:GitCommit"] = "bbb2222",
            ["Config:History:1:AppliedAt"] = "2026-07-05T00:00:00Z",
            ["Config:History:1:AppliedBy"] = "gitops",
        });

        var history = await svc.GetVersionHistoryAsync();

        history.Should().HaveCount(2, "注入した 2 エントリが縮退せず実値で surfacing される");
        history[0].GitCommit.Should().Be("aaa1111"); // AppliedAt 降順（新しい順）
        history[0].HadDrift.Should().BeFalse();       // 注入した hadDrift を保つ
        history[1].GitCommit.Should().Be("bbb2222");
        history[1].HadDrift.Should().BeNull();         // 未注入フィールドは不明（null）
    }

    // 履歴 env 未注入（Helm 既定 config.history: []＝env なし）は現在バージョン単一へ縮退する（後方互換）。
    [Fact]
    public async Task NoHistoryEnvKeys_DegradesToCurrentVersion()
    {
        var svc = BuildFromConfig(new Dictionary<string, string?>
        {
            ["Config:GitCommit"] = "cur9999",
            ["Config:AppliedAt"] = "2026-07-07T00:00:00Z",
            ["Config:AppliedBy"] = "argocd",
        });

        var history = await svc.GetVersionHistoryAsync();

        history.Should().ContainSingle();
        history[0].GitCommit.Should().Be("cur9999");
        history[0].HadDrift.Should().BeNull();
    }
}
