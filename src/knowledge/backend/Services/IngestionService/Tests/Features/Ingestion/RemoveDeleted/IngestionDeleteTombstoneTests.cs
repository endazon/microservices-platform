using AwesomeAssertions;
using IngestionService.Common.Concurrency;
using IngestionService.Domain;
using IngestionService.Features.Ingestion.Ingest;
using IngestionService.Features.Ingestion.RemoveDeleted;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging.Abstractions;
using static IngestionService.Tests.Features.Ingestion.Ingest.IngestionOrderingAndAtomicityTests;

namespace IngestionService.Tests.Features.Ingestion.RemoveDeleted;

// 🔴 FR-02, FR-06, FR-19, IADR-0640 (#2263): **取り込みの削除の墓石。** 作業仕様書の規則 11 の表の Q1〜Q7 を 1 対 1 で写す。
//
// 索引の状態を持つ偽物（`StatefulIndex`。版の印・点・旧版の削除の条件を実装と同じ意味で持つ）の上で、更新（`DocumentUpdatedConsumer`）と
// 削除（`DocumentDeletedConsumer`）を同じ排他（`DocumentIndexLocks`）で動かす。鍵は辞書順 (時刻〔ミリ秒〕, 連番)。
// 変異: 墓石の旗（`Deleted`）を見ないと Q2 が、削除の順序の門を外すと Q4 が、排他を外すと Q6 が赤になる（作業仕様書の変異の表）。
[Trait("TestKind", "Unit")]
public class IngestionDeleteTombstoneTests
{
    private static readonly Guid DocId = Guid.Parse("22630000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset T = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    private const string Body = "# 見出しA\n\n本文アルファ\n\n# 見出しB\n\n本文ベータ";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Harness(StatefulIndex Index, DocumentUpdatedConsumer Updates, DocumentDeletedConsumer Deletes);

    private static Harness Build(DocumentIndexLocks? deleteLocks = null)
    {
        var index = new StatefulIndex();
        var locks = new DocumentIndexLocks();
        var updates = new DocumentUpdatedConsumer(
            new ScriptedReader(Body), new MarkdownChunkingService(), new ScriptedEmbedder(), index, new NoopCompleted(),
            ConsumerTimeoutsForTests.Calls(), ConsumerTimeoutsForTests.Timeouts, TimeProvider.System,
            TestMeterFactory.NewEmbeddingPendingMetrics(), TestMeterFactory.NewBodyMissingMetrics(),
            NullLogger<DocumentUpdatedConsumer>.Instance, locks);
        var deletes = new DocumentDeletedConsumer(index, ConsumerTimeoutsForTests.Calls(), ConsumerTimeoutsForTests.Timeouts,
            deleteLocks ?? locks, NullLogger<DocumentDeletedConsumer>.Instance);
        return new Harness(index, updates, deletes);
    }

    private static DocumentUpdated Updated(DateTimeOffset at, long? sequence)
        => new(DocId, "規程", "published", "storage://b/doc.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["ops"], at, Sequence: sequence);

    private static DocumentDeleted Deleted(DateTimeOffset at, long? sequence) => new(DocId, at, Sequence: sequence);

    [Fact]
    public async Task Q1_削除の後に削除の前の最後の更新が再配送されても取り込み直さない()
    {
        var h = Build();
        await h.Updates.Handle(Updated(T, 8), Ct);
        h.Index.PointsOf(DocId).Should().NotBeEmpty("前提: 取り込んだ");

        await h.Deletes.Handle(Deleted(T.AddSeconds(1), 9), Ct);
        h.Index.PointsOf(DocId).Should().BeEmpty("削除は全コレクションから消す");

        await h.Updates.Handle(Updated(T, 8), Ct);

        h.Index.PointsOf(DocId).Should().BeEmpty("墓石より古い更新は取り込まない（#2263）");
        h.Index.Versions[DocId].Deleted.Should().BeTrue("墓石は残る");
    }

    [Fact]
    public async Task Q2_連番の無い削除と同じミリ秒の連番の無い更新は取り込み直さない()
    {
        var h = Build();
        await h.Updates.Handle(Updated(T, null), Ct);
        await h.Deletes.Handle(Deleted(T, null), Ct);

        await h.Updates.Handle(Updated(T, null), Ct);

        h.Index.PointsOf(DocId).Should().BeEmpty("削除済みの印と等しい鍵の更新は古い（削除は寿命の最後の出来事）");
    }

    [Fact]
    public async Task Q3_削除の後に同じIDで作り直された文書は連番が小さくても取り込む()
    {
        var h = Build();
        await h.Updates.Handle(Updated(T, 8), Ct);
        await h.Deletes.Handle(Deleted(T.AddSeconds(1), 9), Ct);

        await h.Updates.Handle(Updated(T.AddSeconds(2), 1), Ct);

        h.Index.PointsOf(DocId).Should().NotBeEmpty("作り直しは削除より新しい時刻を持つ（連番は 1 から）");
        h.Index.Versions[DocId].Deleted.Should().BeFalse();
    }

    [Fact]
    public async Task Q4_作り直しの後に遅れて届いた古い削除は適用しない()
    {
        var h = Build();
        await h.Updates.Handle(Updated(T.AddSeconds(2), 1), Ct);

        await h.Deletes.Handle(Deleted(T.AddSeconds(1), 9), Ct);

        h.Index.PointsOf(DocId).Should().NotBeEmpty("作り直しより前の削除で、作り直した文書を消さない");
        h.Index.Versions[DocId].Deleted.Should().BeFalse();
    }

    [Fact]
    public async Task Q5_削除の再配送は適用し墓石のまま()
    {
        var h = Build();
        await h.Updates.Handle(Updated(T, 8), Ct);
        await h.Deletes.Handle(Deleted(T.AddSeconds(1), 9), Ct);

        await h.Deletes.Handle(Deleted(T.AddSeconds(1), 9), Ct);

        h.Index.PointsOf(DocId).Should().BeEmpty();
        var mark = h.Index.Versions[DocId];
        mark.Deleted.Should().BeTrue();
        mark.Sequence.Should().Be(9);
        mark.UpdatedAtMs.Should().Be(T.AddSeconds(1).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Q6_更新が門を通った後_印を書く前に削除が届いても_両方の後に点は残らない()
    {
        var h = Build();
        Task? delete = null;
        // 更新の受け口が門を通った後、自分の印を書く直前に削除を走らせる。排他があれば削除は更新の終わりを待つ。
        h.Index.BeforeNextSetVersion = () =>
        {
            delete = h.Deletes.Handle(Deleted(T.AddSeconds(1), 9), Ct);
            return Task.CompletedTask;
        };

        await h.Updates.Handle(Updated(T, 8), Ct);
        await delete!;

        h.Index.PointsOf(DocId).Should().BeEmpty("更新が墓石を自分の印で上書きして点を残す形にしない（#2263）");
        h.Index.Versions[DocId].Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task Q7_陽性対照_印の無い文書の更新は取り込む()
    {
        var h = Build();

        await h.Updates.Handle(Updated(T, 1), Ct);

        h.Index.PointsOf(DocId).Should().NotBeEmpty();
        h.Index.Versions[DocId].Deleted.Should().BeFalse();
    }

    [Fact]
    public async Task 排他は使い終えた文書の項目を残さない()
    {
        var locks = new DocumentIndexLocks();
        using (await locks.AcquireAsync(DocId, Ct))
            locks.TrackedDocuments.Should().Be(1);

        locks.TrackedDocuments.Should().Be(0);
    }

    [Fact]
    public async Task 排他の待ちは取り消せ_取り消した待ちは項目を残さない()
    {
        var locks = new DocumentIndexLocks();
        using var held = await locks.AcquireAsync(DocId, Ct);
        using var cancel = new CancellationTokenSource();
        var waiting = locks.AcquireAsync(DocId, cancel.Token);

        await cancel.CancelAsync();

        await ((Func<Task>)(() => waiting)).Should().ThrowAsync<OperationCanceledException>();
        locks.TrackedDocuments.Should().Be(1, "取り消した待ちの分は数えない（持っている 1 件だけ）");
    }
}
