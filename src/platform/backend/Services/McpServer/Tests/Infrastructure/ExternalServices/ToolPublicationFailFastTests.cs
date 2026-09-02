using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Features.McpClients;
using McpServer.Features.Tools;
using McpServer.Infrastructure.ExternalServices;
using McpServer.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// 🔴 FR-16, UC-08, ADR-0024 §5 (#445): 「検証を通らない構成は適用不可」を **サービスが止まること** で表す。
//
// ここが本試験の主張である —— **検証ロジックが例外を投げること**（ToolPublicationConfigValidatorTests が
// 見ている）と、**その例外が実際にサービスを止めること**は別の主張である。後者を誰も試していないと、
// 例外が握り潰されても CI は緑のままになり、壊れた構成で Web サーバーが起動して
// 「公開されているつもりの公開されていない」状態がエラーログだけを吐きながら継続する。
public class ToolPublicationFailFastTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mcp-pub-{Guid.NewGuid():N}");

    // 起動時検証の対象そのもの: 公開してはならないツール（ai.*）を載せた構成。
    private const string ForbiddenConfig =
        """{"version":"v1","tools":[{"name":"ai.generate","service":"ai-analysis"}]}""";

    private const string ValidConfig =
        """{"version":"v1","tools":[{"name":"retrieval.search","service":"retrieval"}]}""";

    // FR-16 (#445): 逸脱した構成は読み込みの時点で例外になる（前提の確認）。
    [Fact]
    public void 逸脱した公開構成は読み込みで例外になる()
    {
        var act = () => LoaderFor(WriteConfig(ForbiddenConfig)).Load();

        act.Should().Throw<InvalidOperationException>().WithMessage("*ai.generate*");
    }

    // 🔴 FR-16 (#445): その例外が **常駐処理の中で握り潰されない**。
    // 収集の一時失敗と違い、構成の破損は再試行では直らない。伝播させてホストを止める
    // （BackgroundServiceExceptionBehavior の既定は StopHost）。
    [Fact]
    public async Task 構成が逸脱していると収集の常駐処理が停止する()
    {
        var act = async () => await RunRefresherAsync(ForbiddenConfig);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*ai.generate*");
    }

    // FR-16 (#445・陽性対照): 正しい構成なら止まらない。
    // これが無いと、上のテストは「常に落ちる実装」でも緑のままになる。
    [Fact]
    public async Task 正しい構成なら収集の常駐処理は停止しない()
    {
        // 収集先を 1 つも構成していない（全サービスが到達不能なのと同じ）。
        // それでも「構成が正しい限り止まらない」ことを確かめる —— 収集の失敗は次の周期へ持ち越す。
        var run = RunRefresherAsync(ValidConfig);

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        finished.Should().NotBeSameAs(run, "構成が正しい限り常駐処理は落ちない");
    }

    // FR-16 (#445): 構成ファイルが無い場合は落ちない（既定は非公開＝空で起動してよい）。
    // 「壊れている」と「まだ何も公開していない」を取り違えると、公開前の環境が起動できなくなる。
    [Fact]
    public void 構成ファイルが無ければ空の構成として読み込む()
    {
        var config = LoaderFor(Path.Combine(_dir, "does-not-exist.json")).Load();

        config.Tools.Should().BeEmpty();
    }

    private string WriteConfig(string json)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "mcp-publication.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static ToolPublicationConfigLoader LoaderFor(string path)
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ToolPublicationConfigLoader.PathKey] = path })
            .Build());

    // ToolCatalogRefresher.ExecuteAsync を直接駆動する。
    // BackgroundService.StartAsync は ExecuteAsync を待たずに返るため、StartAsync の戻りだけでは
    // 例外の有無を観測できない（これがまさに本番で握り潰しに気づけない理由でもある）。
    private async Task RunRefresherAsync(string configJson)
    {
        var services = new ServiceCollection()
            .AddSingleton<IToolDeclarationSource, EmptyDeclarationSource>()
            .BuildServiceProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ToolCatalogRefresher.IntervalKey] = "10",
            })
            .Build();

        var refresher = new ToolCatalogRefresher(
            services,
            new ToolCatalog(NullLogger<ToolCatalog>.Instance),
            LoaderFor(WriteConfig(configJson)),
            configuration,
            NullLogger<ToolCatalogRefresher>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await refresher.StartAsync(cts.Token);
        await refresher.ExecuteTask!;
    }

    private sealed class EmptyDeclarationSource : IToolDeclarationSource
    {
        public Task<IReadOnlyList<ServiceToolDeclarations>> CollectAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ServiceToolDeclarations>>([]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
