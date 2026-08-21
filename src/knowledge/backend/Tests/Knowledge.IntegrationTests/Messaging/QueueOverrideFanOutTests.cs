using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Knowledge.IntegrationTests.Fixtures;
using MassTransit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IngestionService.Worker.Foundation.Ports;
using WikiService.Api.Foundation.Persistence;
using WikiService.Api.Foundation.Ports;

namespace Knowledge.IntegrationTests.Messaging;

// FR-14, UC-04, ADR-0018, ADR-0027 手順 3, #455 U0e:
// **宣言（pipeline.json）の `queue` で受信エンドポイント名を制御できることを固定する。**
//
// 移行チェックリスト手順 3 は「リスニングキュー名にサービス名を前置する」ことを求めており、
// その手段が `queue` 宣言である。🔴 **宣言でキュー名を制御できることが確かめられていなければ、
// 「手順 3 を宣言で守る」という前提そのものが未検証**である。
//
// 従来あったのは**構成バインドの検査だけ**（宣言値が PipelineStepOptions.Queue へ載ること）。
// 束縛は IConfiguration → POCO の話であり、`registration.Endpoint(e => e.Name = step.Queue)` が
// 実際に MassTransit のトポロジを変えるかは**別の主張**である。本テストは後者を見る。
//
// ── #888（U0c）との関係 ──────────────────────────────
// 同じ退行（competing consumer 化）に 2 つの経路がある:
//   コード経路: サービス実装者が Endpoint(e => e.Name = ...) を誤る → #888 が変異試験で実測
//   宣言経路 : pipeline.json の queue を誤る                      → **本テスト**
// 🔴 **宣言経路のほうが危険である。** pipeline.json は GitOps で配送される運用物であり、
// コードレビューを経ずに変わり得る（「設定変更」として扱われやすい）。
[Trait("Category", "Integration")]
public sealed class QueueOverrideFanOutTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    // ingest（IngestionService）と wiki-sync（WikiService）へ宣言する**同一**のキュー名。
    private const string SharedQueue = "u0e-shared-document-updated";

    private readonly RecordingProbe _probe = new();
    private string _fixturePath = "";

    private IngestionServiceFactory _ingestionRoot = null!;
    private WikiServiceFactory _wikiRoot = null!;
    private WebApplicationFactory<global::IngestionService.Worker.IngestionServiceTestMarker> _ingestion = null!;
    private WebApplicationFactory<global::WikiService.Api.WikiServiceTestMarker> _wiki = null!;
    private HttpClient _ingestionClient = null!;
    private HttpClient _wikiClient = null!;

    public async ValueTask InitializeAsync()
    {
        if (!postgres.IsAvailable || !rabbit.IsAvailable) return;

        _fixturePath = WriteSharedQueueFixture();

        _ingestionRoot = new IngestionServiceFactory(postgres, rabbit);
        _wikiRoot = new WikiServiceFactory(postgres, rabbit);

        // U0d で実測したとおり、Pipeline:ConfigPath は Program.cs が**ビルダ構築中に即座に読む**ため
        // ConfigureAppConfiguration では間に合わない。UseSetting（ホスト構成）で上書きする。
        // WithWebHostBuilder からの UseSetting が基底の値を上書きできることは実測で確認済み。
        _ingestion = _ingestionRoot.WithWebHostBuilder(b =>
        {
            b.UseSetting("Pipeline:ConfigPath", _fixturePath);
            b.ConfigureServices(services =>
            {
                services.AddSingleton(_probe);
                services.RemoveAll<IIngestionVectorStore>();
                services.AddSingleton<IIngestionVectorStore>(sp =>
                    new RecordingVectorStore(sp.GetRequiredService<RecordingProbe>()));
                services.RemoveAll<IDocumentContentReader>();
                services.AddSingleton<IDocumentContentReader, StubContentReader>();
                services.RemoveAll<IEmbeddingService>();
                services.AddSingleton<IEmbeddingService, StubEmbeddingService>();
            });
        });

        _wiki = _wikiRoot.WithWebHostBuilder(b =>
        {
            b.UseSetting("Pipeline:ConfigPath", _fixturePath);
            b.ConfigureServices(services =>
            {
                services.AddSingleton(_probe);
                services.RemoveAll<IWikiJsClient>();
                services.AddSingleton<IWikiJsClient, StubWikiJsClient>();
                services.RemoveAll<IWikiContentReader>();
                services.AddSingleton<IWikiContentReader, StubWikiContentReader>();
            });
        });

        _ingestionClient = _ingestion.CreateClient();
        _wikiClient = _wiki.CreateClient();

        await using var scope = _wiki.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _ingestionClient?.Dispose();
        _wikiClient?.Dispose();
        if (_ingestion is not null) await _ingestion.DisposeAsync();
        if (_wiki is not null) await _wiki.DisposeAsync();
        if (_ingestionRoot is not null) await _ingestionRoot.DisposeAsync();
        if (_wikiRoot is not null) await _wikiRoot.DisposeAsync();
        // 一時ファイルを残さない（消し忘れると次回以降の実行が古い宣言を拾い得る）。
        if (_fixturePath.Length > 0 && File.Exists(_fixturePath)) File.Delete(_fixturePath);
    }

    // ADR-0027 手順 3: 2 購読者へ**同一の queue 名**を宣言すると競合コンシューマになり、
    // 片方だけが受信する。
    //
    // 🔴 **このテストは自己検証的である。**
    //   queue 上書きが効いている  → 同一キューを共有 → **丁度 1 つ**が受信 → 通る
    //   queue 上書きが無視される  → 既定キュー名は別々 → **両方**が受信 → **落ちる**
    // したがって「上書きが効くこと」と「効いた結果 fan-out が壊れること」を同時に証明する。
    // U0d のような別建ての「本当に読み込まれたか」テストは要らない（本テストが兼ねる）。
    [DockerFact]
    public async Task SharedQueueDeclaration_CollapsesFanOut_OnlyOneSubscriberReceives()
    {
        var docId = Guid.NewGuid();
        var evt = new DocumentUpdated(
            DocumentId: docId,
            Title: "u0e キュー共有の統合テスト文書",
            Status: "published",
            MarkdownUri: $"storage://knowledge/{docId}.md",
            Attributes: new Dictionary<string, string> { ["confidentiality"] = "public" },
            Tags: ["u0e"],
            UpdatedAt: DateTimeOffset.UtcNow);

        await using (var scope = _wiki.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBus>().Publish(evt);
        }

        // **どちらかが受信するまで**待つ。
        //
        // 🔴 短絡評価で書かない。`var synced = ingested || await Wait...` と書くと、
        // ingested が真のとき Wiki 側を**一度も見ていない**のに synced == true になり、
        // 変数名が「Wiki を確認した結果」だと嘘をつく。**測っていないものに名前を付けない。**
        var ingestedFirst = await _probe.IngestionUpserts.WaitAsync(docId, TimeSpan.FromSeconds(30));
        var syncedFirst = ingestedFirst
            ? false // 取り込み側が先に受けた。この時点で Wiki 側は見ていない（下の最終判定で見る）
            : await WaitForWikiPageAsync(docId, TimeSpan.FromSeconds(30));

        (ingestedFirst || syncedFirst).Should().BeTrue(
            "同一キューでも「どちらか」は受信する。両方とも受信しないならキュー共有ではなく"
            + "ブローカ接続や宣言の読み込みを疑う");

        // 🔴 **受信したあと猶予を置いてから、もう片方が発火していないことを確かめる。**
        // 「来ない」ことの確認なので、待たずに判定すると単に「まだ来ていない」を見てしまう。
        await Task.Delay(TimeSpan.FromSeconds(5));

        var ingestedFinal = await _probe.IngestionUpserts.WaitAsync(docId, TimeSpan.FromMilliseconds(1));
        var syncedFinal = await WikiPageExistsAsync(docId);

        // 🔴 **どちらが受け取るかは非決定である。** RabbitMQ は共有キューの購読者のうち 1 つへ
        // 配送するが、どちらかは決まらない。「Ingestion が受信する」と書くと半分の確率で落ちる。
        // **「2 つのうち丁度 1 つ」**を assert する。
        var received = new[] { ingestedFinal, syncedFinal }.Count(x => x);
        received.Should().Be(1,
            "同一の queue 名を宣言した 2 購読者は競合コンシューマになり、1 通のメッセージは"
            + " 片方だけへ配送される（ADR-0027 手順 3 が防ごうとしている退行そのもの）。"
            + " 2 なら queue 上書きが効いておらず既定の別キューになっている");
    }

    // 本番 pipeline.json から**実行時に派生**させ、ingest と wiki-sync に同一の queue を入れる。
    //
    // 🔴 **手で書き写さない。** 規則 2 が登録される全段の宣言を要求するため 5 段すべてが要り、
    // 書き写せば本番の宣言が変わったときに腐る（U0d で確立した原則）。
    private static string WriteSharedQueueFixture()
    {
        var source = IntegrationTestFactoryBase<global::WikiService.Api.WikiServiceTestMarker>
            .FindRepoFileForTests(Path.Combine(
                "deploy", "helm", "microservices-platform", "files", "pipeline.json"));

        var root = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        var steps = root["steps"]!.AsArray();
        var patched = 0;
        foreach (var step in steps)
        {
            var name = step!["name"]!.GetValue<string>();
            if (name is "ingest" or "wiki-sync")
            {
                step["queue"] = SharedQueue;
                patched++;
            }
        }

        // 🔴 派生に失敗したら止める。段名が変わっていた場合、上書きが 1 件も当たらないまま
        // 「別々の既定キュー → 両方受信」になり、**テストが落ちた理由を取り違える**。
        if (patched != 2)
        {
            throw new InvalidOperationException(
                $"pipeline.json の ingest / wiki-sync に queue を入れられなかった（当たり {patched} 件）。"
                + " 段名が変わった可能性がある。派生元: " + source);
        }

        var path = Path.Combine(Path.GetTempPath(), $"u0e-pipeline-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private async Task<bool> WikiPageExistsAsync(Guid documentId)
    {
        await using var scope = _wiki.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
        return await db.Pages.AsNoTracking().AnyAsync(p => p.DocumentId == documentId);
    }

    private async Task<bool> WaitForWikiPageAsync(Guid documentId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await WikiPageExistsAsync(documentId)) return true;
            await Task.Delay(250);
        }
        return false;
    }
}
