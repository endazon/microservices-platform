using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;

namespace Knowledge.IntegrationTests.Messaging;

// ADR-0027 手順 8 / #441 E1: **辺 `RawDocumentFetched` が実ブローカ越しに成立すること**を測る。
//
// 手順 8 は「実ブローカでの結合テストを**移行の完了条件に含める**」と定めている。
// 本ファイルが E1 についてその条件を満たす。
//
// 🔴 **主張は 3 つで 1 組である。**
//   (1) 本物の `RawDocumentFetchedConsumer` が受信し、**正規化まで進んで発行口へ到達した**
//   (2) 発行元の囮が受信しなかった —— **ブローカを経由したこと**
//   (3) **型名が発行行に現れない形**（発行 ②）でも同じ経路を通る
//
// (2) が無ければ、規約ローカルルーティングが復活した状態でも (1) は成立し得る。
// (3) が無ければ、静的検査から見えない発行元について**何も測っていない**（変異 A の実測: あの形は
// トポロジ検査の終了コード・報告行・baseline のバイト列を 1 つも動かさない）。
[Trait("Category", "Integration")]
public sealed class RawDocumentFetchedEdgeTests(RabbitMqFixture rabbit) : IClassFixture<RabbitMqFixture>
{
    // 超えたら TimeoutException で**落ちる**（諦めて緑にはならない）。
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(60);

    // 囮の受信数を 0 と断じる前の落ち着き待ち。ローカル配送はブローカ経由より必ず速いので
    // 理屈のうえでは不要だが、「たまたま囮が遅れて 0 に見えた」形を消すために置く。
    private static readonly TimeSpan BaitSettleWindow = TimeSpan.FromSeconds(2);

    private const string ExchangeName = "e1-rawdoc-edge";

    [BrokerFact]
    public async Task 実ブローカ経由でRawDocumentFetchedが本物のハンドラへ届き_発行元へは配送されない()
    {
        rabbit.IsAvailable.Should().BeTrue("BrokerFact が走った以上ブローカは供給されているはず");
        var connectionString = rabbit.ConnectionString;
        connectionString.Should().NotBeNull();

        await using var edge = await RawDocumentFetchedEdge.StartAsync(connectionString!, ExchangeName);

        var sourceId = await edge.PublishAsync();

        // (1) 🔴 「受信した」ではなく「**正規化まで進んで発行口へ到達した**」を待つ。
        // 受信して例外で落ちても購読キューからは消えるので、受信だけでは成功と言えない。
        await ReceiveOrExplain(edge, RawDocumentFetchedEdge.RecordingPublisher.Role, sourceId);

        await Task.Delay(BaitSettleWindow);

        // (2) 🔴 器の要。囮はリスニングを持たないため、ブローカ経由では原理的に受信できない。
        edge.Recorder.CountFor(RawDocumentFetchedEdge.BaitHandler.Role, sourceId).Should().Be(
            0, "発行元の囮が受信したなら、publish がブローカへ出ずプロセス内で配送された");

        edge.Recorder.CountFor(RawDocumentFetchedEdge.RecordingPublisher.Role, sourceId).Should().Be(
            1, "1 回の発行に対し購読先はちょうど 1 通処理する");
    }

    [BrokerFact]
    public async Task 型名が発行行に現れない発行でも同じ経路を通る()
    {
        // 🔴 発行 ②（`ConversionJobEndpoints.cs` の再変換）と同じ形 —— 変数を渡す publish。
        // **静的検査はこの形を見ない**ので、実行時に同じ経路を通ることは実ブローカでしか示せない。
        rabbit.IsAvailable.Should().BeTrue("BrokerFact が走った以上ブローカは供給されているはず");
        var connectionString = rabbit.ConnectionString;
        connectionString.Should().NotBeNull();

        await using var edge = await RawDocumentFetchedEdge.StartAsync(connectionString!, ExchangeName);

        var sourceId = await edge.PublishThroughVariableAsync();

        await ReceiveOrExplain(edge, RawDocumentFetchedEdge.RecordingPublisher.Role, sourceId);

        await Task.Delay(BaitSettleWindow);

        edge.Recorder.CountFor(RawDocumentFetchedEdge.BaitHandler.Role, sourceId).Should().Be(
            0, "型名の可視・不可視は配送経路を変えない（変えるなら移行の前提が崩れる）");
        edge.Recorder.CountFor(RawDocumentFetchedEdge.RecordingPublisher.Role, sourceId).Should().Be(1);
    }

    // タイムアウトだけ見せられても原因に辿り着けないので、誰が何通受けたかを添えて落とす。
    private static async Task ReceiveOrExplain(RawDocumentFetchedEdge edge, string role, Guid correlationId)
    {
        try
        {
            await edge.Recorder.Received(role, correlationId).WaitAsync(ReceiveTimeout);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"{role} が {ReceiveTimeout.TotalSeconds} 秒以内に受信しなかった。"
                + $" 受信状況: {edge.Recorder.Snapshot(correlationId)}", ex);
        }
    }
}
