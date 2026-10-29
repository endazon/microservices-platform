using IngestionService.Common.Concurrency;
using AwesomeAssertions;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Features.Ingestion.Ingest;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace IngestionService.Tests.Features.Ingestion.Ingest;

// FR-02, FR-05, UC-04, [[IADR-0595]] (#2101): **取り込みの順序と原子性。**
//
// 索引の状態を持つ偽物（`StatefulIndex`。版の印・コレクションごとの点・旧版の削除の条件を実装と同じ意味で持つ）の上で、
// 次の場面を固定する:
//   - 到着順の逆転（新しい版の後に古い版が届く）—— 古い版は反映されず、判定軸を厳しくした版が緩まない
//   - 再配送（同じ版が 2 度届く）—— 点は増えず、同じ内容に収束する
//   - 撤収・削除の後に古い版が届く —— 点は書き戻されない
//   - 本文の取得・書き込みの失敗 —— 判定軸が同じなら旧版が残る（文書が消えない）。変わったなら旧版は残らない（fail-closed）
//   - 新版を書き終えた後の後始末 —— チャンク数の減少・置き場所の移動で旧版の点が残らない
//   - 取り込みの最中に新しい版が反映された —— 追い越された版の点は残らない
//
// 🔴 **陰性（載らない）の主張には陽性対照を対で置く**（索引が空だっただけの緑にしない）。
[Trait("TestKind", "Unit")]
public class IngestionOrderingAndAtomicityTests
{
    private static readonly Guid DocId = Guid.Parse("21010000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset V1 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset V2 = V1.AddMinutes(5);

    private const string TwoChunks = "# 見出しA\n\n本文アルファ\n\n# 見出しB\n\n本文ベータ";
    private const string ThreeChunks = "# A\n\nいち\n\n# B\n\nに\n\n# C\n\nさん";
    private const string OneChunk = "# A\n\n改訂後の本文";

    private static DocumentUpdated Event(DateTimeOffset updatedAt, string confidentiality = "internal",
        string status = "published", string? markdownUri = "storage://b/doc.md", List<string>? sharedWith = null,
        long? sequence = null)
        => new(DocId, "規程", status, markdownUri,
            new Dictionary<string, string> { ["confidentiality"] = confidentiality }, ["ops"], updatedAt,
            SharedWith: sharedWith, Sequence: sequence);

    private static DocumentUpdatedConsumer Build(StatefulIndex index, ScriptedReader reader, ScriptedEmbedder? embed = null)
        => new(
            reader,
            new MarkdownChunkingService(),
            embed ?? new ScriptedEmbedder(),
            index,
            new NoopCompleted(),
            ConsumerTimeoutsForTests.Calls(),
            ConsumerTimeoutsForTests.Timeouts,
            TimeProvider.System,
            TestMeterFactory.NewEmbeddingPendingMetrics(),
            TestMeterFactory.NewBodyMissingMetrics(),
            NullLogger<DocumentUpdatedConsumer>.Instance,
            new DocumentIndexLocks(),
            QdrantBootstrapStateForTests.Ensured());

    private static Task Handle(DocumentUpdatedConsumer consumer, DocumentUpdated ev)
        => consumer.Handle(ev, TestContext.Current.CancellationToken);

    // ── FR-19, ADR-0142 決定 1・4, IADR-0636 (#2138): 論理削除（撤収の形）と復元。どちらも版（UpdatedAt）を進めない ──

    // 個人資料（横断検索 ON・共有あり）の更新。撤収の形は DocumentService の唯一の発行点と同じ関数（`DocumentExposure.WithAllExcluded`）で作る。
    private static DocumentUpdated NoteEvent(DateTimeOffset updatedAt, bool withdrawn)
    {
        var attributes = new Dictionary<string, string>
        {
            [DocumentScopes.Key] = DocumentScopes.PrivateNote,
            ["owner"] = "alice",
            ["confidentiality"] = "internal",
        };
        foreach (var (key, value) in DocumentExposure.Project(includeInSearch: true, includeInGraph: false, includeInAi: true))
            attributes[key] = value;
        return new(DocId, "個人メモ", "normalized", "storage://b/note.md",
            withdrawn ? DocumentExposure.WithAllExcluded(attributes) : attributes, [], updatedAt, SharedWith: ["bob"]);
    }

    [Fact]
    public async Task 論理削除の撤収で全索引から消え_同じ版の復元で載り直す()
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));

        await Handle(consumer, NoteEvent(V1, withdrawn: false));
        index.PointsOf(DocId).Should().NotBeEmpty("陽性対照: 削除の前は共有先の検索に載る");

        await Handle(consumer, NoteEvent(V1, withdrawn: true));
        index.PointsOf(DocId).Should().BeEmpty("論理削除は全コレクション（語彙索引を含む）から点を消す");

        // 🔴 境界: 復元は版を進めない（撤収と同じ版）。等しい版は反映する（IADR-0595）ので、載り直す。
        await Handle(consumer, NoteEvent(V1, withdrawn: false));
        index.PointsOf(DocId).Should().NotBeEmpty("復元で元の露出のまま載り直す（ADR-0142 決定 4）");
        index.PointsOf(DocId).Should().OnlyContain(p => p.UpdatedAtMs == V1.ToUnixTimeMilliseconds());
    }

    // 撤収の後に古い版（削除の前に出た更新の再配送）が届いても載り直さない。
    [Fact]
    public async Task 論理削除の撤収の後に古い版が届いても載り直さない()
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));

        await Handle(consumer, NoteEvent(V2, withdrawn: false));
        await Handle(consumer, NoteEvent(V2, withdrawn: true));
        index.PointsOf(DocId).Should().BeEmpty();

        await Handle(consumer, NoteEvent(V1, withdrawn: false));

        index.PointsOf(DocId).Should().BeEmpty("撤収した版より古い更新は反映しない");
    }

    // ── 到着順の逆転 ──

    // 🔴 GraphService の「保持中より古いイベントは適用されない_厳格化後に緩和が復活しない」を取り込みへ写す。
    // 新しい版（confidential へ厳格化）の後に古い版（internal）が届いても、索引は新しい版のまま残る。
    [Fact]
    public async Task 保持中より古い更新は反映されない_厳格化後に緩和が復活しない()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);

        await Handle(consumer, Event(V2, "confidential"));
        index.PointsOf(DocId).Should().NotBeEmpty("陽性対照: 新しい版は索引される");

        await Handle(consumer, Event(V1, "internal"));

        index.PointsOf(DocId).Should().OnlyContain(p => p.Confidentiality == "confidential",
            "古い版（internal）が新しい版（confidential）を上書きしてはならない");
        index.PointsOf(DocId).Should().OnlyContain(p => p.UpdatedAtMs == V2.ToUnixTimeMilliseconds());
        index.PointsOf(DocId).Should().OnlyContain(p => p.Collection == StatefulIndex.LexicalName,
            "confidential は語彙索引だけに載る（古い版の埋め込みのコレクションの点が復活しない）");
        reader.Reads.Should().Be(1, "古い版は本文を読む前に弾かれる");
        index.Versions[DocId].UpdatedAtMs.Should().Be(V2.ToUnixTimeMilliseconds(), "版の印は新しい版のまま");
    }

    // 共有先を外した版の後に、外す前の版が届いても、共有先は復活しない（判定軸は機密区分だけではない）。
    [Fact]
    public async Task 共有先を外した版の後に古い版が届いても共有先は復活しない()
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));

        await Handle(consumer, Event(V2, sharedWith: []));
        await Handle(consumer, Event(V1, sharedWith: ["user-x"]));

        index.PointsOf(DocId).Should().NotBeEmpty();
        index.PointsOf(DocId).Should().OnlyContain(p => p.SharedWith.Count == 0, "外した共有先が古い版で戻らない");
    }

    // ── 再配送 ──

    // 同じ版が 2 度届いても点は増えず、同じ内容に収束する（等しい版は反映する —— 再配送・再発行は同じ版を運ぶ）。
    [Fact]
    public async Task 同じ版の再配送は点を増やさず同じ内容に収束する()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);

        await Handle(consumer, Event(V1));
        var first = index.PointsOf(DocId).Select(p => (p.Collection, p.Id, p.Text)).ToList();

        await Handle(consumer, Event(V1));

        reader.Reads.Should().Be(2, "等しい版は弾かない（再配送・再発行を反映する）");
        index.PointsOf(DocId).Select(p => (p.Collection, p.Id, p.Text)).Should().BeEquivalentTo(first);
        first.Should().HaveCount(2);
    }

    // ── 撤収・削除の後に古い版が届く ──

    // 🔴 アーカイブ（撤収）した後に、撤収前の古い版が届いても点は書き戻されない。陽性対照: 撤収前は索引されていた。
    [Fact]
    public async Task 撤収した後に古い版が届いても点は書き戻されない()
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));

        await Handle(consumer, Event(V1));
        index.PointsOf(DocId).Should().NotBeEmpty("陽性対照: 撤収前は索引されている");

        await Handle(consumer, Event(V2, status: DocumentArchival.ArchivedStatus));
        index.PointsOf(DocId).Should().BeEmpty();

        await Handle(consumer, Event(V1));

        index.PointsOf(DocId).Should().BeEmpty("撤収した版より古い版は反映しない");
    }

    // 🔴 文書の削除（検索側が点を全部消す）の後に、削除前より古い版が遅れて届いても点は書き戻されない
    // （版の印は点と別の場所にあり、点を消しても残る）。
    [Fact]
    public async Task 削除で点が消えた後に古い版が届いても点は書き戻されない()
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));

        await Handle(consumer, Event(V2));
        index.PointsOf(DocId).Should().NotBeEmpty("陽性対照");

        index.RemoveAllPointsOf(DocId); // 検索側（RetrievalService）の削除の受け口が点を消した状態

        await Handle(consumer, Event(V1));

        index.PointsOf(DocId).Should().BeEmpty("削除前の版より古い版は反映しない");
    }

    // ── 失敗しても消えない／緩まない ──

    // 🔴 判定軸が同じ更新で本文の取得が失敗しても（再試行を使い切りデッドレターへ落ちても）、旧版の点は残る。
    [Fact]
    public async Task 判定軸が同じ更新で本文の取得が失敗しても旧版の点は残る()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);

        await Handle(consumer, Event(V1));
        var before = index.PointsOf(DocId).Select(p => p.Text).ToList();
        before.Should().HaveCount(2);

        reader.FailNext = true;
        var act = () => Handle(consumer, Event(V2));
        await act.Should().ThrowAsync<IOException>();

        index.PointsOf(DocId).Select(p => p.Text).Should().BeEquivalentTo(before, "文書は索引から消えない");
    }

    // 🔴 判定軸が同じ更新で書き込みが途中で失敗しても、旧版の点は残る（書き換わったチャンクは新版、残りは旧版）。
    [Fact]
    public async Task 判定軸が同じ更新で書き込みが途中で失敗しても文書は索引から消えない()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);

        await Handle(consumer, Event(V1));

        reader.Body = ThreeChunks;
        index.FailAfter(1); // ここから 2 点目の書き込みで失敗する
        var act = () => Handle(consumer, Event(V2));
        await act.Should().ThrowAsync<IOException>();

        index.PointsOf(DocId).Should().HaveCount(2, "旧版の点を先に消していないので、文書は索引に残る");
    }

    // 🔴 判定軸が変わる更新（internal → confidential）で本文の取得が失敗したら、旧版（緩い判定軸）の点は残らない（fail-closed）。
    // 陽性対照: 判定軸が同じ更新の失敗では旧版が残る（上の試験）。
    [Fact]
    public async Task 判定軸が変わる更新で失敗したら旧版の緩い点は残らない()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);

        await Handle(consumer, Event(V1, "internal"));
        index.PointsOf(DocId).Should().NotBeEmpty();

        reader.FailNext = true;
        var act = () => Handle(consumer, Event(V2, "confidential"));
        await act.Should().ThrowAsync<IOException>();

        index.PointsOf(DocId).Should().BeEmpty("判定軸を変える更新は旧版を先に消す（失敗しても緩い判定軸の点を残さない）");

        // 再試行（同じ版の再配送）は成功すれば新しい判定軸で載る。
        reader.FailNext = false;
        await Handle(consumer, Event(V2, "confidential"));
        index.PointsOf(DocId).Should().NotBeEmpty().And.OnlyContain(p => p.Confidentiality == "confidential");
    }

    // 印の無い文書（移行前に索引された文書）は判定軸が「不明」なので、先に消す（従前の挙動・fail-closed）。
    [Fact]
    public async Task 版の印が無い文書は旧版を先に消す()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);

        index.Seed("voyage", ChunkId.Derive(DocId, 7), DocId, updatedAtMs: null, "internal"); // 移行前の点（版を持たない）
        reader.FailNext = true;
        var act = () => Handle(consumer, Event(V1));
        await act.Should().ThrowAsync<IOException>();

        index.PointsOf(DocId).Should().BeEmpty();
    }

    // ── 書き終えた後の後始末 ──

    // チャンク数が減った版（3 → 1）を書き終えたら、旧版の余ったチャンクは消える（先に全消ししない形でも残らない）。
    [Fact]
    public async Task チャンク数が減った版を書き終えると旧版の余りの点は消える()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(ThreeChunks);
        var consumer = Build(index, reader);

        await Handle(consumer, Event(V1));
        index.PointsOf(DocId).Should().HaveCount(3);

        reader.Body = OneChunk;
        await Handle(consumer, Event(V2));

        index.PointsOf(DocId).Should().ContainSingle().Which.Text.Should().Contain("改訂後の本文");
    }

    // 埋め込み待ち（語彙索引）だった版の後に埋め込める版が来たら、語彙索引の待ちの点は消える（置き場所の移動）。
    // 同じ版の再発行（版が等しい）でも残らない —— 後始末は「この取り込みで書いていない点」を消す。
    [Fact]
    public async Task 置き場所が移った同じ版の再発行でも元の置き場所の点は残らない()
    {
        var index = new StatefulIndex();
        var embed = new ScriptedEmbedder { Embedded = false };
        var consumer = Build(index, new ScriptedReader(TwoChunks), embed);

        await Handle(consumer, Event(V1));
        index.PointsOf(DocId).Should().OnlyContain(p => p.Collection == StatefulIndex.LexicalName && p.Pending);

        embed.Embedded = true;
        await Handle(consumer, Event(V1));

        index.PointsOf(DocId).Should().HaveCount(2).And.OnlyContain(p => p.Collection == "voyage" && !p.Pending);
    }

    // ── 追い越し ──

    // 🔴 古い版の取り込みの最中に新しい版が反映されたら、古い版の点は残らない（新しい版の点だけが残る）。
    // 本文の取得の中で新しい版（confidential）を丸ごと取り込み、古い版（internal）はその後に書き込む —— 印の読み直しで追い越しを知る。
    [Fact]
    public async Task 取り込みの最中に新しい版が反映されたら古い版の点は残らない()
    {
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);
        var nested = Build(index, new ScriptedReader(OneChunk));
        reader.OnRead = () => Handle(nested, Event(V2, "confidential"));

        await Handle(consumer, Event(V1, "internal"));

        index.PointsOf(DocId).Should().ContainSingle()
            .Which.Should().Match<StatefulIndex.Point>(p => p.Confidentiality == "confidential"
                                                            && p.UpdatedAtMs == V2.ToUnixTimeMilliseconds());
    }

    // ── 🔴 FR-02, FR-19, IADR-0639 (#2150 / #2251) 段 2: 規則 11 の表の P1〜P4（取り込み）──
    //
    // 鍵は辞書順 (時刻〔ミリ秒〕, 連番)。時刻はミリ秒より細かい端数（+0.5 ms）を持たせる —— 偽物の版の印は実装と同じくミリ秒へ丸めて
    // 保存するので、丸めを外すと同じ時刻の古い版を端数の分だけ新しいと読み、P1・P3 が赤になる。
    // P5（削除と更新の交差）は取り込みに無い: 取り込みは `DocumentDeleted` を受けない（点の削除は Retrieval。作業仕様書の残余）。
    // ［2026-10-26 追記 / #2263］取り込みも `DocumentDeleted` を受けるようになった（IADR-0640）。削除と更新の交差は `IngestionDeleteTombstoneTests` の Q1〜Q7 が写す。
    // P2 は「削除の後の作り直し」の取り込み側の形（印は削除の前の最後の更新を持ち続ける）で表明する。

    private static readonly DateTimeOffset Tf = V1.AddTicks(5_000);

    [Fact]
    public async Task P1_同時刻の逆転_連番の小さい古い版は反映しない()
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));
        await Handle(consumer, Event(Tf, sharedWith: ["new"], sequence: 6));

        await Handle(consumer, Event(Tf, sharedWith: ["old"], sequence: 5));

        index.PointsOf(DocId).Should().NotBeEmpty().And.OnlyContain(p => p.SharedWith.SequenceEqual(new[] { "new" }),
            "同じ時刻で連番が小さいのは古い版（#2150。共有の取り消しが古い共有先で上書きされない）");
        index.Versions[DocId].Sequence.Should().Be(6);
    }

    [Fact]
    public async Task P1_陽性対照_同時刻で連番が大きい新しい版は反映する()
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));
        await Handle(consumer, Event(Tf, sharedWith: ["old"], sequence: 5));

        await Handle(consumer, Event(Tf, sharedWith: ["new"], sequence: 6));

        index.PointsOf(DocId).Should().NotBeEmpty().And.OnlyContain(p => p.SharedWith.SequenceEqual(new[] { "new" }));
    }

    [Fact]
    public async Task P2_削除の後に同じIDで作り直された文書は連番が小さくても反映する()
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));
        await Handle(consumer, Event(Tf, sharedWith: ["before"], sequence: 8));
        index.RemoveAllPointsOf(DocId); // 削除（点は Retrieval が消す。版の印は残る）

        await Handle(consumer, Event(Tf.AddSeconds(1), sharedWith: ["recreated"], sequence: 1));

        index.PointsOf(DocId).Should().NotBeEmpty().And.OnlyContain(p => p.SharedWith.SequenceEqual(new[] { "recreated" }),
            "作り直しは削除より新しい時刻を持つ（連番は 0 から始まる）");
    }

    [Fact]
    public async Task P3_撤収の後に遅れて届いた古いONは載り直さない()
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));
        await Handle(consumer, NoteEvent(Tf, withdrawn: false) with { Sequence = 5 });
        await Handle(consumer, NoteEvent(Tf, withdrawn: true) with { Sequence = 6 });
        index.PointsOf(DocId).Should().BeEmpty("前提: 撤収した");

        await Handle(consumer, NoteEvent(Tf, withdrawn: false) with { Sequence = 5 });

        index.PointsOf(DocId).Should().BeEmpty("撤収の後に古い ON が後着しても載り直さない（#2251）");
    }

    [Fact]
    public async Task P3_遅れて届いた古いOFFは新しい版を撤収しない()
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));
        await Handle(consumer, NoteEvent(Tf, withdrawn: false) with { Sequence = 6 });

        await Handle(consumer, NoteEvent(Tf, withdrawn: true) with { Sequence = 5 });

        index.PointsOf(DocId).Should().NotBeEmpty("古い撤収で新しい版を消さない");
    }

    [Theory]
    [InlineData(null, 3L)]
    [InlineData(3L, null)]
    public async Task P4_どちらかに連番が無ければ時刻だけで比べ_同時刻は反映する(long? first, long? second)
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));
        await Handle(consumer, Event(Tf, sharedWith: ["first"], sequence: first));

        await Handle(consumer, Event(Tf, sharedWith: ["second"], sequence: second));

        index.PointsOf(DocId).Should().NotBeEmpty().And.OnlyContain(p => p.SharedWith.SequenceEqual(new[] { "second" }),
            "旧形式（連番なし）が混ざれば現行どおり時刻で比べ、同じ時刻は反映する");
    }

    [Fact]
    public async Task P4_時刻が古ければ連番が大きくても反映しない()
    {
        var index = new StatefulIndex();
        var consumer = Build(index, new ScriptedReader(TwoChunks));
        await Handle(consumer, Event(Tf, sharedWith: ["new"], sequence: 3));

        await Handle(consumer, Event(Tf.AddMilliseconds(-1), sharedWith: ["old"], sequence: 9));

        index.PointsOf(DocId).Should().OnlyContain(p => p.SharedWith.SequenceEqual(new[] { "new" }));
    }

    [Fact]
    public async Task 取り込みの途中に同じ時刻の新しい版が反映されたら_古い版の後始末は新しい版の点を消さない()
    {
        // IADR-0639 決定 3: 書き終えた後の読み直しと旧版の点の後始末を、同じ辞書順にする。
        var index = new StatefulIndex();
        var reader = new ScriptedReader(TwoChunks);
        var consumer = Build(index, reader);
        var nested = Build(index, new ScriptedReader(OneChunk));
        reader.OnRead = () => Handle(nested, Event(Tf, "confidential", sequence: 6));

        await Handle(consumer, Event(Tf, "internal", sequence: 5));

        index.PointsOf(DocId).Should().ContainSingle("同じ時刻でも連番の大きい新しい版の点だけが残る")
            .Which.Should().Match<StatefulIndex.Point>(p => p.Confidentiality == "confidential" && p.Sequence == 6);
    }

    [Fact]
    public async Task 連番を持たない旧点は時刻だけで比べ_同じ時刻なら新しい版の後始末で消える()
    {
        var index = new StatefulIndex();
        index.Seed("voyage", Guid.NewGuid(), DocId, Tf.ToUnixTimeMilliseconds(), "internal"); // 移行前の点（連番なし）
        index.Versions[DocId] = new DocumentIndexVersion(Tf, IndexAccessFingerprint.Of(
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, null));
        var consumer = Build(index, new ScriptedReader(OneChunk));

        await Handle(consumer, Event(Tf, sequence: 6));

        index.PointsOf(DocId).Should().OnlyContain(p => p.Sequence == 6, "旧点は現行と同じに消える（連番が無いので時刻だけで比べる）");
    }

    // ── 純関数 ──

    [Fact]
    public void 判定はミリ秒で比べ_等しい版と印の無い文書は反映する()
    {
        var indexed = new DocumentIndexVersion(V2, "x");
        DocumentIndexOrdering.IsStale(indexed, V1, null).Should().BeTrue();
        DocumentIndexOrdering.IsStale(indexed, V2, null).Should().BeFalse();
        DocumentIndexOrdering.IsStale(indexed, V2.AddTicks(9_000), null).Should().BeFalse("ミリ秒未満の差は等しい版として読む");
        DocumentIndexOrdering.IsStale(indexed, V2.AddMilliseconds(-1), null).Should().BeTrue();
        DocumentIndexOrdering.IsStale(null, V1, null).Should().BeFalse();
    }

    // 🔴 FR-06, IADR-0640 決定 2 (#2263): 墓石の判定（グラフの `GraphSyncOrdering` と同じ規則）。
    [Fact]
    public void 墓石と等しい鍵の更新は古く_墓石より古い削除は適用せず_等しい削除は適用する()
    {
        var tomb = DocumentIndexVersion.Tombstone(Tf, 9);
        DocumentIndexOrdering.IsStale(tomb, Tf, 9).Should().BeTrue("削除済みの印と等しい鍵の更新は古い");
        DocumentIndexOrdering.IsStale(tomb, Tf, null).Should().BeTrue("連番が無ければ時刻だけ。等しければ古い");
        DocumentIndexOrdering.IsStale(tomb, Tf.AddMilliseconds(1), 1).Should().BeFalse("作り直し（新しい時刻）は反映する");
        DocumentIndexOrdering.IsStale(new DocumentIndexVersion(Tf, "fp", 9), Tf, 9).Should().BeFalse("削除済みでなければ等しい版は反映する（従前どおり）");

        var live = new DocumentIndexVersion(Tf.AddSeconds(2), "fp", 1);
        DocumentIndexOrdering.IsStaleDelete(live, Tf.AddSeconds(1), 9).Should().BeTrue("作り直しより前の削除は適用しない");
        DocumentIndexOrdering.IsStaleDelete(tomb, Tf, 9).Should().BeFalse("削除の再配送は適用する");
        DocumentIndexOrdering.IsStaleDelete(null, Tf, 9).Should().BeFalse("印が無ければ適用する");
    }

    [Fact]
    public void 判定軸の指紋は鍵と共有先の順に依らず_値が変われば変わる()
    {
        var a = IndexAccessFingerprint.Of(new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" }, ["u", "v"]);
        var b = IndexAccessFingerprint.Of(new Dictionary<string, string> { ["y"] = "2", ["x"] = "1" }, ["v", "u", "u"]);
        a.Should().Be(b);
        IndexAccessFingerprint.Of(new Dictionary<string, string> { ["x"] = "1", ["y"] = "3" }, ["u", "v"]).Should().NotBe(a);
        IndexAccessFingerprint.Of(new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" }, ["u"]).Should().NotBe(a);
        IndexAccessFingerprint.Of(new Dictionary<string, string>(), null)
            .Should().Be(IndexAccessFingerprint.Of(new Dictionary<string, string>(), []));
    }

    // ── 部品 ──

    // 索引の状態を持つ偽物。点の削除・版の印・旧版の削除の条件は実装（`QdrantIngestionVectorStore`）と同じ意味で持つ。
    internal sealed class StatefulIndex : IIngestionVectorStore
    {
        public const string LexicalName = "lexical";

        public sealed record Point(string Collection, Guid Id, Guid DocumentId, long? UpdatedAtMs, string Confidentiality,
            IReadOnlyList<string> SharedWith, string Text, bool Pending, long? Sequence = null);

        private readonly Dictionary<(string Collection, Guid Id), Point> _points = [];
        public Dictionary<Guid, DocumentIndexVersion> Versions { get; } = [];
        private int? _failAfter;
        private int _upserts;

        // ここから数えて n 点を書いた後の書き込みを失敗させる。
        public void FailAfter(int n)
        {
            _failAfter = n;
            _upserts = 0;
        }

        public IReadOnlyList<Point> PointsOf(Guid documentId) => [.. _points.Values.Where(p => p.DocumentId == documentId)];

        public void RemoveAllPointsOf(Guid documentId)
        {
            foreach (var key in _points.Where(p => p.Value.DocumentId == documentId).Select(p => p.Key).ToList())
                _points.Remove(key);
        }

        public void Seed(string collection, Guid id, Guid documentId, long? updatedAtMs, string confidentiality)
            => _points[(collection, id)] = new Point(collection, id, documentId, updatedAtMs, confidentiality, [], "seed", false);

        private Task Write(string collection, Guid id, Guid documentId, DateTimeOffset? updatedAt,
            Dictionary<string, string> attributes, List<string>? sharedWith, string text, bool pending, long? sequence)
        {
            if (_failAfter is { } n && _upserts >= n)
                throw new IOException("qdrant write failed");
            _upserts++;
            _points[(collection, id)] = new Point(collection, id, documentId, updatedAt?.ToUnixTimeMilliseconds(),
                attributes.GetValueOrDefault("confidentiality") ?? "", sharedWith ?? [], text, pending, sequence);
            return Task.CompletedTask;
        }

        public Task EnsureCollectionsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title, string text,
            int chunkIndex, float[] vector, string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, long? sequence = null, CancellationToken ct = default)
            => Write(collection, chunkId, documentId, updatedAt, attributes, sharedWith, text, false, sequence);

        public Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId, string title,
            string indexText, float[] vector, string? markdownUri, Dictionary<string, string> attributes,
            List<string> tags, DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            long? sequence = null, CancellationToken ct = default)
            => Write(collection, pointId, documentId, updatedAt, attributes, sharedWith, indexText, false, sequence);

        public Task UpsertLexicalChunkAsync(Guid chunkId, Guid documentId, string title, string text, int chunkIndex,
            string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, long? sequence = null, CancellationToken ct = default)
            => Write(LexicalName, chunkId, documentId, updatedAt, attributes, sharedWith, text, false, sequence);

        public Task UpsertLexicalMetadataPointAsync(Guid pointId, Guid documentId, string title, string indexText,
            string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null, long? sequence = null, CancellationToken ct = default)
            => Write(LexicalName, pointId, documentId, updatedAt, attributes, sharedWith, indexText, false, sequence);

        public Task UpsertEmbeddingPendingChunkAsync(Guid chunkId, Guid documentId, string title, string text,
            int chunkIndex, string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt, List<string>? sharedWith, string reason, long? sequence = null, CancellationToken ct = default)
            => Write(LexicalName, chunkId, documentId, updatedAt, attributes, sharedWith, text, true, sequence);

        public Task UpsertEmbeddingPendingMetadataPointAsync(Guid pointId, Guid documentId, string title,
            string indexText, string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt, List<string>? sharedWith, string reason, long? sequence = null, CancellationToken ct = default)
            => Write(LexicalName, pointId, documentId, updatedAt, attributes, sharedWith, indexText, true, sequence);

        public Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default)
        {
            RemoveAllPointsOf(documentId);
            return Task.CompletedTask;
        }

        public Task<DocumentIndexVersion?> GetIndexVersionAsync(Guid documentId, CancellationToken ct = default)
            => Task.FromResult(Versions.TryGetValue(documentId, out var v) ? v : null);

        // IADR-0640 (#2263): 版の印を書く直前に 1 度だけ走る割り込み（更新の受け口が門を通った後・自分の印を書く前の窓を開ける）。
        public Func<Task>? BeforeNextSetVersion { get; set; }

        public async Task SetIndexVersionAsync(Guid documentId, DocumentIndexVersion version, CancellationToken ct = default)
        {
            if (BeforeNextSetVersion is { } hook)
            {
                BeforeNextSetVersion = null;
                await hook();
            }
            Versions[documentId] = version with { UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(version.UpdatedAtMs) };
        }

        // 実装のフィルタと同じ条件: 文書の点 ∧ ¬(updated_at > version) ∧ ¬(同じ時刻で点と版の両方に連番があり点の連番が大きい) ∧ ¬(この取り込みで書いた点)。
        public Task DeleteSupersededAsync(Guid documentId, DateTimeOffset version, long? sequence,
            IReadOnlyCollection<IndexedPointRef> keep, CancellationToken ct = default)
        {
            var ms = version.ToUnixTimeMilliseconds();
            var kept = keep.Select(k => (k.Collection ?? LexicalName, k.PointId)).ToHashSet();
            foreach (var key in _points
                         .Where(p => p.Value.DocumentId == documentId
                                     && !(p.Value.UpdatedAtMs is { } at && at > ms)
                                     && !(sequence is { } seq && p.Value.Sequence is { } ps && p.Value.UpdatedAtMs == ms && ps > seq)
                                     && !kept.Contains(p.Key))
                         .Select(p => p.Key).ToList())
                _points.Remove(key);
            return Task.CompletedTask;
        }
    }

    internal sealed class ScriptedReader(string body) : IDocumentContentReader
    {
        public string Body { get; set; } = body;
        public bool FailNext { get; set; }
        public Func<Task>? OnRead { get; set; }
        public int Reads { get; private set; }

        public async Task<string> ReadAsync(string markdownUri, string title, CancellationToken ct = default)
        {
            Reads++;
            if (FailNext)
                throw new IOException("storage unavailable");
            if (OnRead is { } hook)
            {
                OnRead = null;
                await hook();
            }

            return Body;
        }
    }

    // public / internal は埋め込みのコレクション `voyage` へ（`Embedded=false` なら一時障害として埋め込み待ちへ）。
    internal sealed class ScriptedEmbedder : IEmbeddingService
    {
        public bool Embedded { get; set; } = true;

        public Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct = default)
            => Task.FromResult(Embedded
                ? new EmbeddingResult([0.1f, 0.2f], "voyage", true)
                : new EmbeddingResult([], "", Embedded: false, Retryable: true));
    }

    internal sealed class NoopCompleted : IIngestionCompletedPublisher
    {
        public Task PublishCompletedAsync(Guid documentId, int chunkCount, DateTimeOffset completedAt,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
