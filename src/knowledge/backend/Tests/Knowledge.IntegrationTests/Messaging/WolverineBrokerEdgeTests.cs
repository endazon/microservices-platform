using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Knowledge.IntegrationTests.Messaging;

// ADR-0027 手順 8（実ブローカ結合テスト）の器そのものを試験する（#455 W3）。
//
// 本ファイルは「移行したイベント辺が実ブローカ越しに発行・購読されること」を証明する器の
// 最小の実例であり、後続の全辺 PR（E1〜E3b）が同じ形を使う。
//
// 🔴 **器の主張は 3 つで 1 組である。**
//   (1) 購読 A が受信した      —— 届いたこと
//   (2) 購読 B が受信した      —— fan-out が保存されていること（E3b の要件）
//   (3) 発行元の囮が受信しなかった —— **ブローカを経由したこと**
//
// (3) が無ければ、この器はデュアルスタック期間のリスクを 1 ミリも塞がない。
// 規約ローカルルーティングが復活した状態でも (1)(2) だけなら成立し得る形を作ってはならない。
[Trait("Category", "Integration")]
public sealed class WolverineBrokerEdgeTests(RabbitMqFixture rabbit) : IClassFixture<RabbitMqFixture>
{
    // 受信を待つ上限。超えたら TimeoutException で**落ちる**（諦めて緑にはならない）。
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(60);

    // 囮の受信数を 0 と断じる前に置く落ち着き待ち。
    // ローカル配送はブローカ経由より必ず速いため理屈のうえでは不要だが、
    // 「たまたま囮が遅れて 0 に見えた」形を消すために 1 回だけ置く。
    private static readonly TimeSpan BaitSettleWindow = TimeSpan.FromSeconds(2);

    private const string ExchangeName = "w3-broker-edge";

    [Fact]
    public async Task 実ブローカ経由で1回の発行が2購読先へ届き_発行元へは配送されない()
    {
        BrokerRequired.SkipUnlessObtainable();
        rabbit.IsAvailable.Should().BeTrue("BrokerRequired.SkipUnlessObtainable() を通過した以上ブローカは供給されているはず");
        var connectionString = rabbit.ConnectionString;
        connectionString.Should().NotBeNull();

        await using var edge = await WolverineBrokerEdge.StartAsync(connectionString!, ExchangeName);

        var correlationId = await edge.PublishAsync("w3-real-broker");

        // (1)(2): 2 購読先が**同じ 1 通**を受け取る。片方でも来なければ落ちる。
        await ReceiveOrExplain(edge, IngestionEdgeHandler.Role, correlationId);
        await ReceiveOrExplain(edge, WikiEdgeHandler.Role, correlationId);

        await Task.Delay(BaitSettleWindow, TestContext.Current.CancellationToken);

        // (3) 🔴 器の要。囮はリスニングを持たないため、ブローカ経由では原理的に受信できない。
        // 1 通でも受けていたら、発行がプロセス内へ閉じたということである。
        edge.Recorder.CountFor(PublisherLocalBaitHandler.Role, correlationId).Should().Be(
            0,
            "発行元の囮ハンドラが受信したなら、publish がブローカへ出ずプロセス内で配送された");

        edge.Recorder.CountFor(IngestionEdgeHandler.Role, correlationId).Should().Be(
            1, "1 回の発行に対し購読 A はちょうど 1 通受け取る");
        edge.Recorder.CountFor(WikiEdgeHandler.Role, correlationId).Should().Be(
            1, "1 回の発行に対し購読 B はちょうど 1 通受け取る（fan-out の保存）");
    }

    // 手順 4 の**陽性対照**。共通既定を当てなければ、発行はプロセス内へ閉じて囮が受信する。
    //
    // 🔴 このテストが無いと、次の「受信しない」テストは無意味になる。囮が壊れて二度と
    // 発火しなくなっても「受信 0 件」で緑になるからである。**検出器が生きていることを先に示す。**
    [Fact]
    public async Task 共通既定を当てなければ発行はプロセス内へ閉じ囮が受信する()
    {
        BrokerRequired.SkipUnlessObtainable();
        var connectionString = rabbit.ConnectionString;
        connectionString.Should().NotBeNull();

        await using var probe = await WolverineBrokerEdge.StartLocalRoutingProbeAsync(
            connectionString!, applyPlatformDefaults: false);

        var (correlationId, publishError) = await probe.TryPublishAsync("w3-local-control");
        publishError.Should().BeNull("規約ローカルルーティングが経路を与えるため送信は成功する");

        await probe.Recorder.Received(PublisherLocalBaitHandler.Role, correlationId)
            .WaitAsync(ReceiveTimeout, TestContext.Current.CancellationToken);

        probe.Recorder.CountFor(PublisherLocalBaitHandler.Role, correlationId).Should().Be(
            1, "共通既定が無ければ publish はプロセス内のハンドラへ配送される");
    }

    // 手順 4 の**本体**。共通既定を当てれば、同じ形でも囮は受信しない。
    // これが W3 が塞ぐリスクの核心である —— デュアルスタック期間に publish が
    // 黙ってプロセス内へ閉じ、外部購読者へ 1 通も出て行かない形。
    [Fact]
    public async Task 共通既定を当てれば発行はプロセス内へ閉じない()
    {
        BrokerRequired.SkipUnlessObtainable();
        var connectionString = rabbit.ConnectionString;
        connectionString.Should().NotBeNull();

        await using var probe = await WolverineBrokerEdge.StartLocalRoutingProbeAsync(
            connectionString!, applyPlatformDefaults: true);

        var (correlationId, _) = await probe.TryPublishAsync("w3-local-guard");

        // 送信が例外になったかどうかは問わない。経路が無いこと自体は正しい結果である。
        // 問うのは 1 点だけ ——「プロセス内へ配送されなかったか」。
        await Task.Delay(BaitSettleWindow, TestContext.Current.CancellationToken);

        probe.Recorder.CountFor(PublisherLocalBaitHandler.Role, correlationId).Should().Be(
            0,
            "共通既定（手順 4）が効いていれば、発行元に同じ型のハンドラがあっても"
            + "publish はプロセス内へ閉じない");
    }

    // タイムアウトを「どの役が受け取らなかったか」まで含めて報告する。
    private static async Task ReceiveOrExplain(WolverineBrokerEdge edge, string role, Guid correlationId)
    {
        try
        {
            await edge.Recorder.Received(role, correlationId).WaitAsync(ReceiveTimeout);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException(
                $"役 {role} が {ReceiveTimeout.TotalSeconds} 秒以内に受信しなかった。"
                + $" exchange={edge.ExchangeName} 記録={edge.Recorder.Snapshot(correlationId)}");
        }
    }

    // --- 以下はブローカを要さない self-test（器の前提そのものを固定する） ---

    // 手順 3: 2 購読先のリスニングキューが**別々**であること。
    // 同一キューになると RabbitMQ の競合コンシューマへ退行し、片方だけが受信する。
    [Fact]
    public void 器の2購読先はサービス名前置により別々のキューを購読する()
    {
        var ingestion = WolverineExtensions.PlatformQueueName(
            IngestionEdgeHandler.Role, WolverineBrokerEdge.EventName);
        var wiki = WolverineExtensions.PlatformQueueName(
            WikiEdgeHandler.Role, WolverineBrokerEdge.EventName);

        ingestion.Should().NotBe(wiki, "同一キューだと fan-out が competing consumer へ退行する");
        ingestion.Should().StartWith(IngestionEdgeHandler.Role);
        wiki.Should().StartWith(WikiEdgeHandler.Role);
    }

    // 器の skip 条件が「ブローカが本当に無いとき」だけであること。
    // 外部エンドポイントが設定されているのに skip する形になっていたら、
    // 設定した人は「通った」と誤読する。
    [Fact]
    public void 外部ブローカが設定されていればDockerが無くても実走する()
    {
        var externallyProvided = RabbitMqFixture.ExternalEndpoint is not null;
        if (!externallyProvided)
        {
            Assert.Skip($"{RabbitMqFixture.ExternalEndpointVariable} 未設定のため判定対象外");
        }

        BrokerRequired.IsObtainable().Should().BeTrue(
            "外部エンドポイントが設定されているなら Docker の有無に関わらず実走させる");
    }
}
