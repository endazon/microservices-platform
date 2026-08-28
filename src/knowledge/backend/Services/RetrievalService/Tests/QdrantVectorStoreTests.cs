using RetrievalService.Infrastructure.ExternalServices;
using RetrievalService.Domain.Ports;
using AwesomeAssertions;
using Qdrant.Client.Grpc;

namespace RetrievalService.Tests;

// FR-05, FR-11 (Issue #58 の #2 / Issue #71): Qdrant ペイロードから ABAC 属性を DTO へ復元することを検証する。
// 復元しないと機密区分判定が常に「属性欠落 → restricted」へ縮退し、FR-11 の機密区分別ルーティングが
// 事実上無効化される。IADR-0014（実機検証・選択肢C）によりネスト構造体表現へ統一済み。
public class QdrantVectorStoreTests
{
    // ネスト構造体表現: 書き込み（UpsertAsync）と同じ `attributes -> { k: v }` から復元できる。
    [Fact]
    public void ExtractAttributes_RestoresFromNestedStruct()
    {
        var nested = new Value { StructValue = new Struct() };
        nested.StructValue.Fields["confidentiality"] = new Value { StringValue = "restricted" };
        nested.StructValue.Fields["department"] = new Value { StringValue = "legal" };

        var payload = new Dictionary<string, Value>
        {
            ["text"] = new() { StringValue = "本文" },
            ["attributes"] = nested,
        };

        var attrs = QdrantVectorStore.ExtractAttributes(payload);

        attrs["confidentiality"].Should().Be("restricted");
        attrs["department"].Should().Be("legal");
    }

    // FR-03, SC-02, #536（IADR-0149 決定 1）: `updated_at`（Unix epoch ミリ秒の整数）を復元する。
    // 書き込み側（取り込み・検索の 2 経路）と同じ表現から読む。
    [Fact]
    public void ExtractUpdatedAt_RestoresFromEpochMilliseconds()
    {
        var updatedAt = new DateTimeOffset(2026, 8, 9, 3, 0, 0, TimeSpan.Zero);
        var payload = new Dictionary<string, Value>
        {
            ["text"] = new() { StringValue = "本文" },
            ["updated_at"] = new() { IntegerValue = updatedAt.ToUnixTimeMilliseconds() },
        };

        QdrantVectorStore.ExtractUpdatedAt(payload).Should().Be(updatedAt);
    }

    // **本項目より前に索引されたチャンクは日時を持たない。**null を返すことがその状態を正しく表す
    // （IADR-0149 決定 3）。DateTimeOffset.MinValue で埋めると「知らない」が「とても古い」に化け、
    // 並び順（#532）が嘘をつく。再索引が済むまでの縮退であって障害ではない。
    [Fact]
    public void ExtractUpdatedAt_WhenNotIndexed_ReturnsNull()
    {
        var payload = new Dictionary<string, Value>
        {
            ["document_id"] = new() { StringValue = "doc" },
            ["text"] = new() { StringValue = "本文" },
        };

        QdrantVectorStore.ExtractUpdatedAt(payload).Should().BeNull();
    }

    // 整数以外が入っていた場合も null へ倒す（文字列で書かれた旧データ・手作業の混入を黙って
    // 誤った日時にしない）。
    [Fact]
    public void ExtractUpdatedAt_WhenNotAnInteger_ReturnsNull()
    {
        var payload = new Dictionary<string, Value>
        {
            ["updated_at"] = new() { StringValue = "2026-08-09T03:00:00Z" },
        };

        QdrantVectorStore.ExtractUpdatedAt(payload).Should().BeNull();
    }

    // 属性を持たないペイロードでは空辞書を返す（機密区分判定側は欠落を安全側 restricted に倒す）。
    [Fact]
    public void ExtractAttributes_WhenNoAttributes_ReturnsEmpty()
    {
        var payload = new Dictionary<string, Value>
        {
            ["document_id"] = new() { StringValue = "doc" },
            ["text"] = new() { StringValue = "本文" },
        };

        QdrantVectorStore.ExtractAttributes(payload).Should().BeEmpty();
    }

    // IADR-0014（選択肢C）以前にフラットキー `attributes.{k}` で書き込まれた既存データは、
    // 再取込（DocumentUpdated 再発行）まで属性を復元できず空辞書となる。これは安全側
    // （欠落 → restricted、deny-by-default）であり、漏えい方向には倒れない。
    [Fact]
    public void ExtractAttributes_LegacyFlatKeyPayload_ReturnsEmptyUntilReingested()
    {
        var payload = new Dictionary<string, Value>
        {
            ["text"] = new() { StringValue = "本文" },
            ["attributes.confidentiality"] = new() { StringValue = "confidential" },
        };

        QdrantVectorStore.ExtractAttributes(payload).Should().BeEmpty();
    }

    // --- FR-03, SC-02（Issue #642）: タグの書き込みと復元 -------------------------------
    // **本番の欠陥は復元側である** —— MapPayload が `Tags: []` を固定で返していたため、
    // SC-02 の結果一覧（SearchResultsPage のタグ列）が本番でのみ常に空欄になっていた。
    // InMemoryVectorStore は ChunkPayload.Tags をそのまま運ぶのでテストは緑のままであり、
    // IADR-0014 が名指しした「テストは緑・本番は空」と同型である。
    //
    // **書き込み側（BuildPayload）の 3 件は予防である** —— UpsertAsync を呼ぶ本番コードは無く
    // （書いているのは IngestionService 側）、直しても現時点の本番挙動は変わらない。
    // それでも固定するのは、この口を使い始めたときに表現が割れていないことを保証するためである。

    // 取り込み側（QdrantIngestionVectorStore.BuildChunkPayload）と同じ `tags -> ListValue` から復元する。
    [Fact]
    public void ExtractTags_RestoresFromListValue()
    {
        var payload = new Dictionary<string, Value>
        {
            ["text"] = new() { StringValue = "本文" },
            ["tags"] = TagList("経理", "規程"),
        };

        QdrantVectorStore.ExtractTags(payload).Should().Equal("経理", "規程");
    }

    // タグを持たない文書は空リスト（画面はタグ列を空欄にする。0 件は正常な状態である）。
    [Fact]
    public void ExtractTags_WhenNoTags_ReturnsEmpty()
    {
        var payload = new Dictionary<string, Value>
        {
            ["document_id"] = new() { StringValue = "doc" },
            ["text"] = new() { StringValue = "本文" },
        };

        QdrantVectorStore.ExtractTags(payload).Should().BeEmpty();
    }

    // `tags` がリストでない（手で投入された等）場合も検索全体を失敗させず空リストに倒す。
    [Fact]
    public void ExtractTags_WhenTagsNotAList_ReturnsEmpty()
    {
        var payload = new Dictionary<string, Value>
        {
            ["tags"] = new() { StringValue = "経理,規程" },
        };

        QdrantVectorStore.ExtractTags(payload).Should().BeEmpty();
    }

    // 非文字列スカラーは文字列化し、非スカラー（構造体）は読み飛ばす。
    [Fact]
    public void ExtractTags_CoercesScalarsAndSkipsNonScalars()
    {
        var list = new ListValue();
        list.Values.Add(new Value { IntegerValue = 42 });
        list.Values.Add(new Value { BoolValue = true });
        list.Values.Add(new Value { StructValue = new Struct() });

        var payload = new Dictionary<string, Value> { ["tags"] = new() { ListValue = list } };

        QdrantVectorStore.ExtractTags(payload).Should().Equal("42", "true");
    }

    // 書き込みの表現が取り込み側と一致すること。
    // 期待の形は IngestionService 側の QdrantIngestionVectorStoreTests と同じ主張である
    // （片側の表現を変えればもう片側が赤くなる）。
    [Fact]
    public void BuildPayload_WritesTagsInIngestionRepresentation()
    {
        var payload = QdrantVectorStore.BuildPayload(Chunk(["経理", "規程"]));

        payload["tags"].ListValue.Values.Select(v => v.StringValue).Should().Equal("経理", "規程");
    }

    // タグ 0 件ではキー自体を書かない（attributes と同じ扱い・取り込み側とも一致する）。
    [Fact]
    public void BuildPayload_WhenNoTags_OmitsTagsKey()
    {
        var payload = QdrantVectorStore.BuildPayload(Chunk([]));

        payload.ContainsKey("tags").Should().BeFalse();
    }

    // ★ 書き込みと復元の**表現が一致している**ことを往復で固定する。
    // **「どちらが欠けても本番でタグが出ない」ではない** —— 本番の欠陥は復元側だけであり
    // （クラス冒頭のとおり書き込み側に呼び出し元は無い）、この往復は表現の一致を守る面である。
    [Fact]
    public void BuildPayloadThenExtractTags_RoundTrips()
    {
        var payload = QdrantVectorStore.BuildPayload(Chunk(["経理", "規程"]));

        QdrantVectorStore.ExtractTags(payload).Should().Equal("経理", "規程");
    }

    private static Value TagList(params string[] tags)
    {
        var list = new ListValue();
        foreach (var t in tags)
            list.Values.Add(new Value { StringValue = t });
        return new Value { ListValue = list };
    }

    private static ChunkPayload Chunk(List<string> tags) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "経費精算規程 v3.2", "本文", [0f], "s3://bucket/a.md", [], tags);
}
