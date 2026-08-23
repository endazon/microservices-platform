using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Platform.Shared.Infrastructure.Foundation.Introspection;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Introspection;

// FR-15, ADR-0018 (#444): 構成情報 API は「宣言」を突合の基準として要求する。
//
// 🔴 **宣言の読み込みは「指定はあるが読めなかった」を黙って許す**（段ホストがローカルで既定配線へ
// 縮退できるようにするための仕様）。構成情報 API 側でそれが起きると宣言 0 件が基準になり、
// 実効の購読すべてが UndeclaredSubscription として並ぶ —— #146 / #118 監査で実際に起きた回帰である。
// 当時の是正は compose / Helm のマウント配線を静的に検査するもので、**読む側には防壁が無かった**。
public class ConfigInspectionDeclarationGuardTests
{
    // FR-15 (#444): ConfigPath を指定しているのに宣言が空なら起動を止める（マウント漏れ）。
    [Fact]
    public void 宣言のパスを指定しているのに読めなければ起動に失敗する()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryPipelinePath(
            Path.Combine(Path.GetTempPath(), "no-such-pipeline-444.json"));

        var act = () => builder.AddPlatformConfigInspection();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*段を 1 件も*", "赤いログを読む人に、何が読めなかったのかが伝わる必要がある");
    }

    // FR-15 (#444): 実在するが段を持たない宣言も同じく止める（空ファイル・形式違い）。
    [Fact]
    public void 宣言のパスは読めても段が0件なら起動に失敗する()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipeline-empty-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"version":1,"events":["RawDocumentFetched"],"steps":[]}""");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryPipelinePath(path);

            var act = () => builder.AddPlatformConfigInspection();

            act.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    // FR-15 (#444): 対照条件その 1。ConfigPath 未指定は正当な構成であり、止めない
    // （宣言なしのローカル・単体試験。ここを止めると既存の起動経路を壊す）。
    [Fact]
    public void 宣言のパスを指定していなければ起動する()
    {
        var builder = Host.CreateApplicationBuilder();

        var act = () => builder.AddPlatformConfigInspection();

        act.Should().NotThrow();
    }

    // FR-15 (#444): 対照条件その 2。段を持つ宣言なら通る。これが無いと「常に落ちる」実装でも上が通る。
    [Fact]
    public void 段を持つ宣言を読めれば起動する()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipeline-ok-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {"version":1,"events":["RawDocumentFetched","DocumentNormalized"],
             "steps":[{"name":"convert","service":"conversion-service",
                       "consumer":"Ns.Convert","input":"RawDocumentFetched",
                       "outputs":["DocumentNormalized"],"enabled":true}]}
            """);
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryPipelinePath(path);

            var act = () => builder.AddPlatformConfigInspection();

            act.Should().NotThrow();
        }
        finally
        {
            File.Delete(path);
        }
    }
}

// 宣言の読み込み先（Pipeline:ConfigPath）だけを与える最小の構成ヘルパ。
internal static class PipelinePathConfigurationExtensions
{
    internal static IConfigurationBuilder AddInMemoryPipelinePath(
        this IConfigurationBuilder configuration, string path) =>
        configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Pipeline:ConfigPath"] = path });
}
