using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.Extensions.Configuration;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

namespace Knowledge.IntegrationTests.Messaging;

// FR-14, UC-04, ADR-0018, ADR-0027, #455 U0d:
// 統合テストが **pipeline.json の段宣言を実際に読み込んで起動している**ことを固定する。
//
// 🔴 **このテストが本作業の要である。** AddPlatformPipelineConfig は
//   if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return builder;
// と**黙って何もせずに返る**ため、Pipeline:ConfigPath の解決に失敗しても例外は出ず、
// 宣言が 1 行も読まれないまま**全テストが緑のまま**になる。
// 「設定したつもりで何も検査していない」状態が成功と見分けられないので、
// **宣言が実際に載っていること自体を assert する**。
//
// これが無ければ U0d は「緑を増やしただけ」になる。
[Trait("Category", "Integration")]
public sealed class PipelineDeclarationLoadedTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private WikiServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        if (!postgres.IsAvailable || !rabbit.IsAvailable) return ValueTask.CompletedTask;
        _factory = new WikiServiceFactory(postgres, rabbit);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    // 正本 pipeline.json の段は 5 件（convert / catalog / ingest / wiki-sync / wiki-delete）。
    // 件数を直接固定するのではなく、**宣言が読み込まれていること**と
    // **本サービスが使う段が宣言に居ること**の両方を見る。
    [DockerFact]
    public void PipelineDeclaration_IsActuallyLoaded()
    {
        var cfg = _factory.Services.GetRequiredService<IConfiguration>();
        var pipeline = cfg.GetPlatformPipeline();

        pipeline.Steps.Should().NotBeEmpty(
            "Pipeline:ConfigPath が解決できていれば段宣言が載る。空なら "
            + "AddPlatformPipelineConfig が黙って return しており、"
            + "**段宣言は 1 行も通っていない**（テストが緑でも何も検査していない）");

        pipeline.Steps.Select(s => s.Name).Should().Contain(
            ["convert", "catalog", "ingest", "wiki-sync", "wiki-delete"],
            "正本 pipeline.json の 5 段がそのまま載ること（テストへ複製していないことの裏返し）");
    }

    // 規則 3・4 が実際に効いていることの裏取り。宣言の consumer / input は実装と一致していなければ
    // 起動時に InvalidOperationException になる。ここまで起動できている＝一致している、という含意を
    // テスト名で明示しておく（変異試験でこの含意が正しいことを実測する）。
    [DockerFact]
    public void DeclaredConsumerAndInput_MatchImplementation_OtherwiseHostWouldNotStart()
    {
        var cfg = _factory.Services.GetRequiredService<IConfiguration>();
        var wikiSync = cfg.GetPlatformPipeline().FindStep("wiki-sync");

        wikiSync.Should().NotBeNull("本サービスの段が宣言に存在すること");
        wikiSync!.Consumer.Should().Be(
            "WikiService.Api.Composable.Steps.DocumentSyncConsumer",
            "宣言の consumer 完全名が実装と一致すること（不一致なら起動時に落ちる）");
        wikiSync.Input.Should().Be("DocumentUpdated",
            "宣言の input が IConsumer<TIn> の TIn と一致すること（不一致なら起動時に落ちる）");
    }
}
