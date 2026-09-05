using System.Collections.Concurrent;
using ConversionService.Features.ConversionJobs.Normalize;
using ConversionService.Domain;
using ConversionService.Infrastructure.Persistence;
using ConversionService.Domain.Ports;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Wolverine;
using Wolverine.RabbitMQ;

namespace Knowledge.IntegrationTests.Fixtures;

// ADR-0027 手順 8 / #441 E1: **辺 `RawDocumentFetched` そのもの**を実ブローカ越しに測る器。
//
// 🔴 **W3 の器（`WolverineBrokerEdge`）との違いは、運ぶものが本物であることである。**
// あちらは合成イベント `BrokerEdgeSignal` と合成ハンドラで「器の作法」を確立した。
// 本器は**本物の契約型 `RawDocumentFetched`** を、**本物の `RawDocumentFetchedConsumer`** へ、
// **本番と同じ登録経路**（`AddPlatformWolverineStep` → `ListenToPlatformQueue` →
// `UsePlatformMessagingDefaults`）で届ける。合成ハンドラでは、この経路が壊れても気づけない。
//
// **囮（publisher-local bait）の作法は W3 から引き継ぐ。**
// 発行ホストに `RawDocumentFetched` のハンドラを 1 つ置き、リスニングは一切持たせない。
// 囮が受信したなら、それは publish がブローカへ出ずプロセス内で配送された動かぬ証拠である。
// **囮が無ければ「届いた」だけを見ることになり、デュアルスタック期間の危険を 1 ミリも塞がない。**
//
// ⚠️ **本器が覆わないもの**: 発行 ②（`ConversionJobEndpoints.cs` の再変換）の**配線そのもの**。
// あちらは API ホストの DI 解決を経るため、本器の発行ホストでは代替できない。
// **発行 ② は `ConversionJobEndpointTests` が Wolverine バスの記録で固定する**
// （変異 A の実測どおり、静的検査では見えないため、試験でしか捕まえられない）。
public sealed class RawDocumentFetchedEdge : IAsyncDisposable
{
    // 発行ホストに置く囮。**リスニングを持たない。**
    public sealed class BaitHandler(EdgeRecorder recorder)
    {
        public const string Role = "publisher-local-bait-rawdoc";

        public Task Handle(RawDocumentFetched ev)
        {
            // 🔴 **相関鍵は購読側と必ず同じものにする（SourceId）。**
            // 当初ここは FetchId で記録しており、テストは SourceId で数えていた ——
            // つまり囮の件数は**発火の有無にかかわらず常に 0** で、
            // 「囮は受信しなかった」という表明は**何も検査していなかった**。
            // 陽性対照（変異 E-1）で囮を実際に発火させたときに初めて露見した。
            recorder.Record(Role, ev.SourceId);
            return Task.CompletedTask;
        }
    }

    // 本物のハンドラが「最後まで走った」ことの観測点。
    // 🔴 **受信だけを見ない。** 受信して例外で落ちても、購読キューから消えることに変わりはない。
    // **正規化まで進んで発行口へ到達したこと**を成功の条件にする。
    public sealed class RecordingPublisher(EdgeRecorder recorder) : IDocumentNormalizedPublisher
    {
        public const string Role = "conversion-service-normalized";

        public Task PublishNormalizedAsync(
            Guid documentId, Guid sourceId, string title, string markdownUri,
            IReadOnlyList<string> assetUris, IReadOnlyDictionary<string, string> attributes,
            IReadOnlyList<string> tags, bool hasBody = true,
            string? originalPath = null, string? dataSourceName = null,
            CancellationToken ct = default)
        {
            recorder.Record(Role, sourceId);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedNormalizer : INormalizationService
    {
        public Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw, CancellationToken ct = default) =>
            Task.FromResult(new NormalizationResult(
                DeterministicGuid.ForDocument(raw.SourceId, raw.OriginalPath),
                "storage://normalized/e1.md", [], 0, 0, []));
    }

    // 本番の宣言と同じ形。**`Consumer` は実装の完全名を書く** —— ここがずれると
    // `AddPlatformWolverineStep` の規則 3 が起動を止める（それも本器が確かめる対象である）。
    private static PipelineOptions ProductionShapedPipeline() => new()
    {
        Steps =
        [
            new PipelineStepOptions
            {
                Name = RawDocumentFetchedConsumer.StepName,
                Service = ServiceName,
                Consumer = typeof(RawDocumentFetchedConsumer).FullName!,
                Input = nameof(RawDocumentFetched),
                Outputs = [nameof(DocumentNormalized)],
                Enabled = true,
            },
        ],
    };

    private const string ServiceName = "conversion-service";

    private readonly List<IHost> _hosts = [];
    private IHost? _publisher;

    // 🔴 実行ごとに一意にする（W3 と同じ理由）。RabbitMQ の束縛は永続するため、固定名だと
    // 前回の実行が残した束縛や滞留メッセージが今回の結果を左右する。
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    public EdgeRecorder Recorder { get; } = new();

    private string ScopedServiceName => $"{ServiceName}-{_runId}";

    public static async Task<RawDocumentFetchedEdge> StartAsync(string connectionString, string exchangePrefix)
    {
        var exchangeName = $"{exchangePrefix}-{Guid.NewGuid():N}";
        var edge = new RawDocumentFetchedEdge();
        // 🔴 キュー名は手順 3 の適用点（共通ヘルパ）から導く。直書きすると前置漏れを見逃す。
        var queueName = WolverineExtensions.PlatformQueueName(edge.ScopedServiceName, nameof(RawDocumentFetched));

        try
        {
            // 🔴 購読側を先に起こす。束縛が無い exchange への publish は黙って落ちる。
            edge._hosts.Add(await edge.StartSubscriberAsync(connectionString, exchangeName, queueName));
            edge._publisher = await edge.StartPublisherAsync(connectionString, exchangeName, queueName);
            edge._hosts.Add(edge._publisher);
            return edge;
        }
        catch
        {
            await edge.DisposeAsync();
            throw;
        }
    }

    private Task<IHost> StartSubscriberAsync(string connectionString, string exchangeName, string queueName)
        => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = ServiceName;

                // 規約探索を切るのは、同一アセンブリの他のハンドラ（囮を含む）を拾わせないためである。
                // **段の登録は本番と同じ共通ヘルパで行う。**
                opts.Discovery.DisableConventionalDiscovery();
                var step = opts.AddPlatformWolverineStep<RawDocumentFetchedConsumer>(ProductionShapedPipeline());

                opts.UseRabbitMq(new Uri(connectionString))
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .DeclareExchange(exchangeName, ex => ex.BindQueue(queueName));

                // 手順 3 の適用点。宣言に queue があればそれを、無ければイベント型名を使う（本番と同じ式）。
                opts.ListenToPlatformQueue(ScopedServiceName, step?.Queue ?? nameof(RawDocumentFetched));

                // 手順 4・5 ＋ retry/DLQ の共通既定。
                opts.UsePlatformMessagingDefaults();

                opts.Services.AddSingleton(Recorder);
                opts.Services.AddSingleton<INormalizationService>(new FixedNormalizer());
                opts.Services.AddSingleton<IDocumentNormalizedPublisher>(
                    sp => new RecordingPublisher(sp.GetRequiredService<EdgeRecorder>()));
                opts.Services.AddDbContext<ConversionJobDbContext>(
                    o => o.UseInMemoryDatabase($"e1-{_runId}"));
                opts.Services.AddScoped<IConversionJobStore, EfConversionJobStore>();
            })
            .StartAsync();

    private Task<IHost> StartPublisherAsync(string connectionString, string exchangeName, string queueName)
        => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "datasource-service-under-test";

                // 🔴 囮だけを登録する。**リスニングは 1 つも設定しない。**
                opts.Discovery.DisableConventionalDiscovery().IncludeType<BaitHandler>();
                opts.Services.AddSingleton(Recorder);

                opts.UseRabbitMq(new Uri(connectionString))
                    .AutoProvision()
                    .DeclareExchange(exchangeName, ex => ex.BindQueue(queueName));

                opts.PublishMessage<RawDocumentFetched>().ToRabbitExchange(exchangeName);
                opts.UsePlatformMessagingDefaults();
            })
            .StartAsync();

    // 発行 ① の形（型名が発行行に現れる）。
    public Task<Guid> PublishAsync() => PublishCoreAsync(typeNameVisible: true);

    // 🔴 発行 ② の形（**型名が発行行に現れない**）。`ConversionJobEndpoints.cs` の再変換と同じ形である。
    // 静的検査はこの形を見ないので、**実行時に同じ経路を通ることは試験でしか示せない**。
    public Task<Guid> PublishThroughVariableAsync() => PublishCoreAsync(typeNameVisible: false);

    private async Task<Guid> PublishCoreAsync(bool typeNameVisible)
    {
        var sourceId = Guid.NewGuid();
        var ev = Sample(sourceId);
        var bus = _publisher!.Services.GetRequiredService<IMessageBus>();

        if (typeNameVisible)
        {
            await bus.PublishAsync(new RawDocumentFetched(
                ev.FetchId, ev.SourceId, ev.SourceType, ev.OriginalPath, ev.StorageUri,
                ev.ContentType, ev.Attributes, ev.Tags, ev.FetchedAt));
        }
        else
        {
            // 変数を渡す。呼び出し行に型名は現れない。
            await bus.PublishAsync(ev);
        }

        return sourceId;
    }

    private static RawDocumentFetched Sample(Guid sourceId) => new(
        Guid.NewGuid(), sourceId, "filesystem", $"/docs/e1-{sourceId:N}.docx",
        $"storage://bucket/raw/e1-{sourceId:N}.docx", "application/msword",
        new Dictionary<string, string> { ["confidentiality"] = "internal" },
        ["knowledge-mgmt"], DateTimeOffset.UtcNow);

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // 後片付けの失敗で本来の失敗理由を覆い隠さない。
            }

            host.Dispose();
        }

        _hosts.Clear();
    }
}
