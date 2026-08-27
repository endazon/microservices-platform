using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using IngestionService.Worker.Foundation.Ports;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using WikiService.Api.Foundation.Persistence;
using WikiService.Api.Foundation.Ports;
using Wolverine;
using Wolverine.RabbitMQ;

namespace Knowledge.IntegrationTests.Messaging;

// FR-14, UC-04, ADR-0018, ADR-0027 手順 3, #455 U0e:
// **宣言（pipeline.json）の `queue` で受信エンドポイント名を制御できることを固定する。**
//
// 移行チェックリスト手順 3 は「リスニングキュー名にサービス名を前置する」ことを求めており、
// その手段が `queue` 宣言である。🔴 **宣言でキュー名を制御できることが確かめられていなければ、
// 「手順 3 を宣言で守る」という前提そのものが未検証**である。
//
// 🔴 ADR-0027 / E3b: 両購読者は Wolverine になった。**意味論が MassTransit 時代と変わった** ——
// 旧経路（`registration.Endpoint(e => e.Name = step.Queue)`）は宣言値を**そのまま**エンドポイント名に
// したため、2 サービスへ同一の queue を宣言すると競合コンシューマ化できた（旧テストはそれを固定していた）。
// Wolverine 経路の適用点（`WolverineExtensions.ListenToPlatformQueue`）は **queue 宣言にも必ず
// サービス名を前置する**ため、同一の宣言値でもキューは `<service>.<queue>` に分かれ、
// **宣言経路から競合コンシューマを作ること自体が構造的にできなくなった**。
// 本テストはその 2 点 —— ①宣言 queue が実際にリスニングキュー名を変えること、
// ②同一宣言値でも fan-out が保たれること（前置の実効）—— を実ブローカで固定する。
[Trait("Category", "Integration")]
public sealed class QueueOverrideFanOutTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    // ingest（IngestionService）と wiki-sync（WikiService）へ宣言する**同一**のキュー名。
    private const string SharedQueue = "u0e-shared-document-updated";

    private readonly RecordingProbe _probe = new();
    private string _fixturePath = "";
    private IHost? _publisher;

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

        await using (var scope = _wiki.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        // E3b: 発行ホスト。**宣言 queue 名から導いた前置つきキュー**へ束縛した exchange へ出す。
        // 宣言が効いていなければ購読者は既定キュー（<svc>.DocumentUpdated）を聴いており、
        // ここで束縛したキューには誰もいない → 両方受信せず下の assert が落ちる（自己検証）。
        var exchange = $"u0e-override-{Guid.NewGuid():N}";
        var ingestionQueue = WolverineExtensions.PlatformQueueName("ingestion-service", SharedQueue);
        var wikiQueue = WolverineExtensions.PlatformQueueName("wiki-service", SharedQueue);
        _publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "document-service-under-test";
                opts.Discovery.DisableConventionalDiscovery();
                opts.UseRabbitMq(new Uri(rabbit.ConnectionString!))
                    .AutoProvision()
                    .DeclareExchange(exchange, ex =>
                    {
                        ex.BindQueue(ingestionQueue);
                        ex.BindQueue(wikiQueue);
                    });
                opts.PublishMessage<DocumentUpdated>().ToRabbitExchange(exchange);
                opts.UsePlatformMessagingDefaults();
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_publisher is not null)
        {
            await _publisher.StopAsync(TimeSpan.FromSeconds(10));
            _publisher.Dispose();
        }
        _ingestionClient?.Dispose();
        _wikiClient?.Dispose();
        if (_ingestion is not null) await _ingestion.DisposeAsync();
        if (_wiki is not null) await _wiki.DisposeAsync();
        if (_ingestionRoot is not null) await _ingestionRoot.DisposeAsync();
        if (_wikiRoot is not null) await _wikiRoot.DisposeAsync();
        // 一時ファイルを残さない（消し忘れると次回以降の実行が古い宣言を拾い得る）。
        if (_fixturePath.Length > 0 && File.Exists(_fixturePath)) File.Delete(_fixturePath);
    }

    // ADR-0027 手順 3 / E3b: 同一の queue 名を宣言しても、適用点（ListenToPlatformQueue）が
    // サービス名を前置するため fan-out は保たれる。
    //
    // 🔴 **このテストは自己検証的である。**
    //   宣言が効いている  → 両購読者が <svc>.u0e-shared-document-updated を聴く → **両方**受信 → 通る
    //   宣言が無視される  → 購読者は既定キュー（<svc>.DocumentUpdated）を聴く →
    //                       発行ホストが束縛したキューには誰もいない → **どちらも受信しない** → 落ちる
    //   前置が退行する    → 2 購読者が素の u0e-shared-document-updated を共有 →
    //                       発行ホストの束縛先（前置つき）には誰もいない → **どちらも受信しない** → 落ちる
    [Fact]
    public async Task SharedQueueDeclaration_KeepsFanOut_ServicePrefixSeparatesQueues()
    {
        DockerRequired.SkipUnlessAvailable();
        var docId = Guid.NewGuid();
        var evt = new DocumentUpdated(
            DocumentId: docId,
            Title: "u0e キュー宣言の統合テスト文書",
            Status: "published",
            MarkdownUri: $"storage://knowledge/{docId}.md",
            Attributes: new Dictionary<string, string> { ["confidentiality"] = "public" },
            Tags: ["u0e"],
            UpdatedAt: DateTimeOffset.UtcNow);

        await _publisher!.Services.GetRequiredService<IMessageBus>().PublishAsync(evt);

        // 両方が受信する（前置により競合コンシューマにならない）。
        var ingested = await _probe.IngestionUpserts.WaitAsync(docId, TimeSpan.FromSeconds(30));
        ingested.Should().BeTrue(
            "IngestionService が宣言 queue（前置つき）で受信すること。受信しないなら"
            + " 宣言が読み込まれていないか、前置（ListenToPlatformQueue）が退行している");

        var synced = await WaitForWikiPageAsync(docId, TimeSpan.FromSeconds(30));
        synced.Should().BeTrue(
            "WikiService も同じ宣言 queue 名から**別の前置つきキュー**で受信すること"
            + "（同一宣言値でも fan-out が保たれる＝手順 3 の実効）");
    }

    // 本番 pipeline.json から**実行時に派生**させ、ingest と wiki-sync に同一の queue を入れる。
    //
    // 🔴 **手で書き写さない。** 規則 2 が登録される全段の宣言を要求するため 5 段すべてが要り、
    // 書き写せば本番の宣言が変わったときに腐る（U0d で確立した原則）。
    private static string WriteSharedQueueFixture()
    {
        var source = RepoFile.Find(Path.Combine(
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
