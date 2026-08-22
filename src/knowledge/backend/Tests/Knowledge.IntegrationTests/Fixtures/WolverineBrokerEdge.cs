using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Wolverine;
using Wolverine.RabbitMQ;

namespace Knowledge.IntegrationTests.Fixtures;

// ADR-0027 移行チェックリスト 手順 8: 実ブローカ結合テストの共通基盤（#455 W3）。
//
// 🔴 **この器が塞ぐのは「publish が黙ってプロセス内へ閉じる」形である。**
// デュアルスタック期間は 1 プロセスに MassTransit バスと Wolverine ホストが同居する。
// 規約ローカルルーティングが効いていると、発行元に同じイベント型のハンドラがあるだけで
// 発行がプロセス内で完結し、外部購読者へ 1 通も出て行かない。これは起動もビルドも
// 封じ込め検査もトポロジ検査も通り、**実行時に無音で壊れる**。
//
// したがって器は「届いた」を見るだけでは足りない。**届かなかったこと**——具体的には
// 発行元に置いた囮ハンドラが 1 通も受け取らなかったこと——を証拠にする。
// 囮はリスニングを一切持たないため、ブローカ経由では原理的に受信できない。
// 囮が受信したなら、それはプロセス内配送が起きた動かぬ証拠である。
//
// 🔴 **［2026-08-22 実測 / #455 W3］明示ルーティングは規約ローカルルーティングより優先される。**
// 当初この器は「exchange への明示ルーティングを持つ発行ホストから共通既定を外せば、
// ローカル配送へ落ちて外部購読者が飢える」と想定していた。**実測すると落ちなかった。**
// opts.PublishMessage<T>().ToRabbitExchange(...) を設定した時点で経路は確定し、
// 手順 4 の有無は挙動に一切出ない（変異 M3 が当たらなかった実記録）。
//
// よって手順 4 の破れは**明示ルーティングを持たないホスト**でしか観測できない。
// それが LocalRoutingProbe である。あわせて「囮が本当に発火し得ること」を示す
// 陽性対照（applyPlatformDefaults: false）を必ず対で置く —— 発火し得ない囮の
// 「受信 0 件」は、何も証明しない無音の穴だからである。
//
// 後続の全辺 PR（E1〜E3b）はこの器を使う。fan-out（2 購読先が同時に受信する）を
// 表現できる形にしてあるのは E3b（DocumentUpdated）の要件である。

// 器が運ぶイベント。**本物の契約型ではない。**
// scripts/check-event-topology.js は `*.Contracts/Events/*.cs` だけを走査し、
// テストパスを除外する（同 :121 / :149）ため、トポロジ台帳には載らない。
public sealed record BrokerEdgeSignal(Guid CorrelationId, string Payload);

// どのホストが何を受信したかの記録。3 ホストで 1 インスタンスを共有する。
public sealed class EdgeRecorder
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Guid>> _byRole = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Guid>> _waiters = new();

    public void Record(string role, Guid correlationId)
    {
        _byRole.GetOrAdd(role, _ => new ConcurrentQueue<Guid>()).Enqueue(correlationId);
        Waiter(role, correlationId).TrySetResult(correlationId);
    }

    // 「その役が、その相関 ID を、何通受け取ったか」。fan-out の退行（competing consumer）は
    // 片側が 0 通になる形で現れるので、bool ではなく件数で持つ。
    public int CountFor(string role, Guid correlationId) =>
        _byRole.TryGetValue(role, out var q) ? q.Count(id => id == correlationId) : 0;

    // 🔴 タイムアウトは**例外**にする（WaitAsync が TimeoutException を投げる）。
    // 「待って諦めて緑」は受信 0 件を成功に見せる無音の穴である。
    public Task<Guid> Received(string role, Guid correlationId) => Waiter(role, correlationId).Task;

    // 失敗時に「誰が何通受けたか」を出す。タイムアウトだけ見せられても原因に辿り着けない。
    public string Snapshot(Guid correlationId)
    {
        var roles = new[]
        {
            PublisherLocalBaitHandler.Role, IngestionEdgeHandler.Role, WikiEdgeHandler.Role,
        };
        var seen = string.Join(", ", roles.Select(r => $"{r}={CountFor(r, correlationId)}"));
        var all = string.Join(" | ", _byRole.Select(kv => $"{kv.Key}:{kv.Value.Count}"));
        return $"[{correlationId:N}] {seen} / 全記録: {all}";
    }

    private TaskCompletionSource<Guid> Waiter(string role, Guid correlationId) =>
        _waiters.GetOrAdd(
            $"{role}/{correlationId:N}",
            _ => new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously));
}

// 発行元に置く囮。**リスニングを持たないホストに登録する。**
// 規約ローカルルーティングが復活したときだけ発火する。
public sealed class PublisherLocalBaitHandler(EdgeRecorder recorder)
{
    public const string Role = "publisher-local-bait";

    public Task Handle(BrokerEdgeSignal signal)
    {
        recorder.Record(Role, signal.CorrelationId);
        return Task.CompletedTask;
    }
}

// 購読側 2 役。**型を分けるのは、ホストごとに探索範囲を絞るためである。**
// 3 ホストは同じテストアセンブリを共有するため、規約探索のままだと全ホストが
// 全ハンドラを拾い、囮設計が濁る。
public sealed class IngestionEdgeHandler(EdgeRecorder recorder)
{
    public const string Role = "ingestion-service";

    public Task Handle(BrokerEdgeSignal signal)
    {
        recorder.Record(Role, signal.CorrelationId);
        return Task.CompletedTask;
    }
}

public sealed class WikiEdgeHandler(EdgeRecorder recorder)
{
    public const string Role = "wiki-service";

    public Task Handle(BrokerEdgeSignal signal)
    {
        recorder.Record(Role, signal.CorrelationId);
        return Task.CompletedTask;
    }
}

public sealed class WolverineBrokerEdge : IAsyncDisposable
{
    // リスニングキュー名の第 2 要素（手順 3 の適用点へ渡す「キュー名」）。
    public const string EventName = nameof(BrokerEdgeSignal);

    private readonly List<IHost> _hosts = [];
    private IHost? _publisher;

    // 🔴 実行ごとに一意な識別子。**キュー名にも入れる。**
    // キュー名を固定にすると、前回の実行が残した束縛やメッセージが今回の結果を左右する。
    // 実測でそれを踏んだ —— 同じコード・同じ変異なしで、フィルタ実行では緑・全体実行では
    // 60 秒タイムアウトという再現性のある食い違いが出た（#455 W3）。
    // 器の緑がブローカの履歴に依存する形は、それ自体が無音の穴である。
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    public EdgeRecorder Recorder { get; } = new();
    public string ExchangeName { get; private set; } = string.Empty;

    // 購読側 2 役。**キュー名は必ず手順 3 の適用点（共通ヘルパ）から導く。**
    // ここで文字列を直書きすると、前置を怠った状態を器が見逃す。
    private static readonly (string ServiceName, Type HandlerType)[] Subscribers =
    [
        (IngestionEdgeHandler.Role, typeof(IngestionEdgeHandler)),
        (WikiEdgeHandler.Role, typeof(WikiEdgeHandler)),
    ];

    private static string QueueFor(string serviceName) =>
        WolverineExtensions.PlatformQueueName(serviceName, EventName);

    // 実行ごとに分離したサービス名。前置の形（<service>.<event>）は変えない。
    private string ScopedServiceName(string role) => $"{role}-{_runId}";

    // 🔴 exchange は**実行ごとに一意**にする。
    // RabbitMQ の束縛は永続するため、固定名だと前回の実行で作られた束縛が残り、
    // 今回の配線が誤っていても配送が成立してしまう（実測。変異 M4 がこれで当たらなかった）。
    // 器が前回の状態に助けられて緑になる形は、無音の穴そのものである。
    public static async Task<WolverineBrokerEdge> StartAsync(string connectionString, string exchangePrefix)
    {
        var exchangeName = $"{exchangePrefix}-{Guid.NewGuid():N}";
        var edge = new WolverineBrokerEdge { ExchangeName = exchangeName };
        var queues = Subscribers.Select(s => QueueFor(edge.ScopedServiceName(s.ServiceName))).ToArray();

        try
        {
            // 🔴 購読側を先に起こす。**発行より前に束縛が存在していなければ、発行した 1 通は
            // 誰にも届かないまま捨てられる**（RabbitMQ の exchange は束縛の無い publish を黙って落とす）。
            // sleep で誤魔化す代わりに、ホスト起動の完了（＝ AutoProvision による宣言と束縛の完了）を待つ。
            foreach (var (serviceName, handlerType) in Subscribers)
            {
                edge._hosts.Add(await edge.StartSubscriberAsync(connectionString, exchangeName, serviceName, handlerType));
            }

            // 発行側は購読側の全キューを束縛したうえで起こす（束縛の取りこぼしを構造的に消す）。
            edge._publisher = await edge.StartPublisherAsync(connectionString, exchangeName, queues);
            edge._hosts.Add(edge._publisher);
            return edge;
        }
        catch
        {
            await edge.DisposeAsync();
            throw;
        }
    }

    private Task<IHost> StartSubscriberAsync(
        string connectionString, string exchangeName, string serviceName, Type handlerType)
    {
        // 手順 3: 購読キュー名はサービス名の前置で分ける。
        // 🔴 **束縛とリスニングは必ず同じ名前から導く。** 別々に書くと、片方だけ誤った状態が
        // 「ブローカに前回の束縛が残っているせいで通る」形で見逃される（実測。#455 W3 の変異 M4）。
        var queueServiceName = ScopedServiceName(serviceName);
        var queueName = QueueFor(queueServiceName);

        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = serviceName;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(handlerType);
                opts.Services.AddSingleton(Recorder);

                opts.UseRabbitMq(new Uri(connectionString))
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .DeclareExchange(exchangeName, ex => ex.BindQueue(queueName));

                // 手順 3 の適用点。ブローカ固有の購読 API を直接呼ばず、必ず共通ヘルパを通す。
                opts.ListenToPlatformQueue(queueServiceName, EventName);

                // 手順 4・5 の共通既定。
                opts.UsePlatformMessagingDefaults();
            })
            .StartAsync();
    }

    private Task<IHost> StartPublisherAsync(
        string connectionString, string exchangeName, IReadOnlyList<string> queues)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "publisher-under-test";

                // 🔴 囮ハンドラだけを登録する。**リスニングは 1 つも設定しない。**
                opts.Discovery.DisableConventionalDiscovery().IncludeType<PublisherLocalBaitHandler>();
                opts.Services.AddSingleton(Recorder);

                opts.UseRabbitMq(new Uri(connectionString))
                    .AutoProvision()
                    .DeclareExchange(exchangeName, ex =>
                    {
                        foreach (var queue in queues)
                        {
                            ex.BindQueue(queue);
                        }
                    });

                opts.PublishMessage<BrokerEdgeSignal>().ToRabbitExchange(exchangeName);

                // 手順 4・5 の共通既定。
                // ⚠️ ここでは手順 4 の効果は観測できない（上のヘッダの実測を参照）。
                // 手順 4 の被験体は StartLocalRoutingProbeAsync のほうである。
                opts.UsePlatformMessagingDefaults();
            })
            .StartAsync();
    }

    // 手順 4（規約ローカルルーティングの無効化）の被験体。
    //
    // **明示ルーティングを持たない**発行ホストを 1 つだけ立てる。この形でのみ、
    // 「発行元に同じイベント型のハンドラがあると publish がプロセス内へ閉じる」が観測できる。
    //
    //   applyPlatformDefaults: false → 囮が受信する（＝ローカル配送が実在することの陽性対照）
    //   applyPlatformDefaults: true  → 囮は受信しない（＝手順 4 が効いている）
    //
    // 対で置くことが要件である。陽性対照が無いと、囮が壊れて発火しなくなっても
    // 「受信 0 件」が成功に見える。
    public static async Task<WolverineBrokerEdge> StartLocalRoutingProbeAsync(
        string connectionString, bool applyPlatformDefaults)
    {
        var edge = new WolverineBrokerEdge();
        try
        {
            edge._publisher = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.ServiceName = "local-routing-probe";
                    opts.Discovery.DisableConventionalDiscovery().IncludeType<PublisherLocalBaitHandler>();
                    opts.Services.AddSingleton(edge.Recorder);

                    // ブローカには繋ぐが、**このイベント型を外へ出す経路は設定しない。**
                    opts.UseRabbitMq(new Uri(connectionString)).AutoProvision();

                    if (applyPlatformDefaults)
                    {
                        opts.UsePlatformMessagingDefaults();
                    }
                })
                .StartAsync();

            edge._hosts.Add(edge._publisher);
            return edge;
        }
        catch
        {
            await edge.DisposeAsync();
            throw;
        }
    }

    // 経路が 1 つも無いとき Wolverine は送信時に例外を投げる。プローブではそれ自体が
    // 「ローカルへも落ちなかった」証拠なので、握り潰さず呼び出し側へ返す。
    public async Task<(Guid CorrelationId, Exception? PublishError)> TryPublishAsync(string payload)
    {
        var correlationId = Guid.NewGuid();
        var bus = _publisher!.Services.GetRequiredService<IMessageBus>();
        try
        {
            await bus.PublishAsync(new BrokerEdgeSignal(correlationId, payload));
            return (correlationId, null);
        }
        catch (Exception ex)
        {
            return (correlationId, ex);
        }
    }

    // fan-out 試験の発行点。経路は exchange への明示ルーティングで確定している。
    public async Task<Guid> PublishAsync(string payload)
    {
        var correlationId = Guid.NewGuid();
        var bus = _publisher!.Services.GetRequiredService<IMessageBus>();
        await bus.PublishAsync(new BrokerEdgeSignal(correlationId, payload)).ConfigureAwait(false);
        return correlationId;
    }

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
                // 後片付けの失敗でテストの本来の失敗理由を覆い隠さない。
            }

            host.Dispose();
        }

        _hosts.Clear();
    }
}
