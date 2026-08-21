using DataSourceService.Api.Foundation.Services;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;

namespace DataSourceService.Api.Tests;

// FR-01, UC-04, IADR-0051, IADR-0074 (#299): Helm が注入する env
//   DataSourceSync__Enabled / DataSourceSync__IntervalSeconds
// が DataSourceSyncOptions へ正しくバインドする契約（＝定期同期が本番/経路B で実際に有効化される配線）を
// 回帰ガードする。env を実際に設定して ASP.NET の __→: 正規化を通し、Helm が出す env 文字列そのものを検証する。
// 環境変数はプロセス全体へ作用するため、相互干渉を避ける非並列コレクションに置く。
[Collection(nameof(EnvVarSerialCollection))]
public sealed class DataSourceSyncOptionsBindingTests
{
    [Fact]
    public void SectionName_MatchesHelmEnvPrefix()
    {
        // env の二重アンダースコア接頭辞（DataSourceSync__）はこのセクション名へ正規化される。
        // セクション名を変えると Helm の env が無言でバインドしなくなるため固定する。
        DataSourceSyncOptions.SectionName.Should().Be("DataSourceSync");
    }

    [Theory]
    [InlineData("true", "300", true, 300)] // 本番 values.yaml（NFR 15 分反映の根拠値）
    [InlineData("true", "60", true, 60)]   // 経路B values-local.yaml（反映確認を高速化）
    public void HelmEnv_BindsTo_Options(string enabled, string interval, bool expEnabled, int expInterval)
    {
        var opt = BindFromEnv(new()
        {
            ["DataSourceSync__Enabled"] = enabled,
            ["DataSourceSync__IntervalSeconds"] = interval,
        });

        opt.Enabled.Should().Be(expEnabled);
        opt.IntervalSeconds.Should().Be(expInterval);
    }

    [Fact]
    public void Absent_Env_KeepsSafeDefaults()
    {
        // dataSourceSync 未設定のサービス（env 非描画）は既定＝無効・300 秒のまま（後方互換・fail-safe）。
        var opt = BindFromEnv(new Dictionary<string, string?>());

        opt.Enabled.Should().BeFalse();
        opt.IntervalSeconds.Should().Be(300);
    }

    private static DataSourceSyncOptions BindFromEnv(Dictionary<string, string?> env)
    {
        // 既存プロセス env の混入を避けるため対象キーを明示クリアしてから設定する。
        Environment.SetEnvironmentVariable("DataSourceSync__Enabled", null);
        Environment.SetEnvironmentVariable("DataSourceSync__IntervalSeconds", null);
        foreach (var (k, v) in env)
            Environment.SetEnvironmentVariable(k, v);
        try
        {
            var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var opt = new DataSourceSyncOptions();
            config.GetSection(DataSourceSyncOptions.SectionName).Bind(opt);
            return opt;
        }
        finally
        {
            Environment.SetEnvironmentVariable("DataSourceSync__Enabled", null);
            Environment.SetEnvironmentVariable("DataSourceSync__IntervalSeconds", null);
        }
    }
}

// 環境変数を操作するテストを他コレクションと同時実行させない（プロセス全体の状態を汚さない）。
[CollectionDefinition(nameof(EnvVarSerialCollection), DisableParallelization = true)]
public sealed class EnvVarSerialCollection;
