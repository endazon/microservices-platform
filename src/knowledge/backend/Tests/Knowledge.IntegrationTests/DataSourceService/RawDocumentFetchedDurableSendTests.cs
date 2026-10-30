using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Knowledge.IntegrationTests.Fixtures;
using Wolverine;
using Wolverine.Runtime;

namespace Knowledge.IntegrationTests.DataSourceService;

// FR-01, UC-04, ADR-0027 §決定, IADR-0609 決定 2 (#2169): データソースの同期が出す `RawDocumentFetched` は
// **耐久送出**（送出前に Outbox の表へ書く）で出る —— DataSourceService の本番の配線（`Program.cs`）そのもので測る。
//
// ［2026-10-30 / #2361］IADR-0609 決定 2（Superseded by IADR-0657）: 送出先が耐久であるだけでは足りなかった —— 表への書き込みが
// 一時的に失敗すると `PublishAsync` は例外なしで返り、再試行の上限で黙って捨てる。同期の発行は 1 件ごとの短い Transactional Outbox になり、
// 書けなければその 1 件が失敗になる（`RawDocumentFetchedStoreFailureTests` が測る）。本試験は送出先が耐久であることだけを測り続ける。
// 以下は当時の記録である。
// 同期の発行は watermark の保存と同じトランザクションに載せない（IADR-0609 決定 2）。要るのは「watermark を進めた時点で、
// それまでの発行が失われない」ことだけで、それは送出先が耐久であることで満たされる（発行のたびに送出前に表へ書く。
// 段 1 の `DocumentDeletedOutboxTests` の「確定の後に送出が失われても再起動後に再送される」が同じ仕組みを実ブローカで測っている）。
// 🔴 `Program.cs` から `UsePlatformTransactionalOutbox` を外すと、送出先は耐久でなくなりここで赤になる
// （既定の送出先は Buffered で、発行はメモリにだけ保持され、プロセスが落ちると watermark だけが進んで原本が恒久に欠落する）。
[Trait("Category", "Integration")]
public sealed class RawDocumentFetchedDurableSendTests(PostgresFixture postgres, RabbitMqFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitMqFixture>
{
    [Fact]
    public async Task 同期の原本イベントの送出先は耐久である()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.Broker);
        await using var factory = new DataSourceServiceFactory(postgres, rabbit);
        _ = factory.Server; // ホストを起こす（Wolverine の送出先は起動時に組み立てられる）。

        var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();
        var bus = factory.Services.GetRequiredService<IMessageBus>();
        var envelopes = bus.PreviewSubscriptions(new RawDocumentFetched(
            Guid.NewGuid(), Guid.NewGuid(), "filesystem", "/docs/a.pdf", "storage://a", "application/pdf",
            new Dictionary<string, string>(), [], DateTimeOffset.UtcNow));

        envelopes.Should().NotBeEmpty("経路が宣言されている（RoutePlatformEvent）");
        foreach (var envelope in envelopes)
            runtime.Endpoints.GetOrBuildSendingAgent(envelope.Destination!).IsDurable
                .Should().BeTrue($"{envelope.Destination} への送出は送出前に Outbox の表へ書かれる");
    }
}
