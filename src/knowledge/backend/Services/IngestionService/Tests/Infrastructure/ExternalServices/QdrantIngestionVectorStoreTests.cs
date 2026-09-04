using IngestionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using IngestionService.Domain.Ports;
using IngestionService.Domain;
using Knowledge.Contracts.Indexing;
using Qdrant.Client.Grpc;

namespace IngestionService.Tests.Infrastructure.ExternalServices;

// FR-05, IADR-0014（選択肢C・実機検証済み・Issue #71）: 取り込み時に書き込む Qdrant ペイロードで
// ABAC 属性がネスト構造体 `attributes -> { k: v }` として構築されることを検証する。
// フラットキー `attributes.{k}` で書き込むと、フィルタ側がドットを JSON パスとして解釈するため
// 過剰除外が発生する（実機検証で確認）。書き込み表現を RetrievalService.QdrantVectorStore と一致させる。
[Trait("TestKind", "Unit")]
public class QdrantIngestionVectorStoreTests
{
    private static Dictionary<string, Value> Build(
        Dictionary<string, string>? attributes = null, List<string>? tags = null,
        DateTimeOffset? updatedAt = null) =>
        QdrantIngestionVectorStore.BuildChunkPayload(
            documentId: Guid.NewGuid(),
            title: "タイトル",
            text: "本文",
            chunkIndex: 3,
            markdownUri: "s3://bucket/doc.md",
            attributes: attributes ?? new Dictionary<string, string>(),
            tags: tags ?? new List<string>(),
            updatedAt: updatedAt);

    // FR-03, SC-02, #536（IADR-0149 決定 1）: 更新日時は `updated_at` へ **Unix epoch ミリ秒の整数**で書く。
    // 文字列（ISO-8601）にすると、同じ時刻を `+09:00` とも `Z` とも書けるため辞書順が実時刻順と一致せず、
    // 並び順（#532）が表記の揺れで壊れる。
    [Fact]
    public void BuildChunkPayload_WritesUpdatedAtAsEpochMilliseconds()
    {
        var updatedAt = new DateTimeOffset(2026, 8, 9, 12, 34, 56, TimeSpan.FromHours(9));

        var payload = Build(updatedAt: updatedAt);

        payload.Should().ContainKey("updated_at");
        payload["updated_at"].KindCase.Should().Be(Value.KindOneofCase.IntegerValue);
        payload["updated_at"].IntegerValue.Should().Be(updatedAt.ToUnixTimeMilliseconds());
    }

    // オフセット表記が違っても**同じ瞬間なら同じ値**になる（整数で持つことの目的そのもの）。
    [Fact]
    public void BuildChunkPayload_UpdatedAtIsIndependentOfOffsetNotation()
    {
        var jst = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.FromHours(9));
        var utc = jst.ToUniversalTime();

        Build(updatedAt: jst)["updated_at"].IntegerValue
            .Should().Be(Build(updatedAt: utc)["updated_at"].IntegerValue);
    }

    // 更新日時を渡さなければキーを置かない（IADR-0149 決定 3）。既定値で埋めると
    // 「知らない」が「とても古い」に化ける。
    [Fact]
    public void BuildChunkPayload_WhenNoUpdatedAt_OmitsTheKey()
    {
        Build().Should().NotContainKey("updated_at");
    }

    // ABAC 属性はネスト構造体として格納される（フラットキー `attributes.{k}` にはしない）。
    [Fact]
    public void BuildChunkPayload_WritesAttributesAsNestedStruct()
    {
        var payload = Build(new Dictionary<string, string>
        {
            ["confidentiality"] = "restricted",
            ["department"] = "legal",
        });

        payload.Should().ContainKey("attributes");
        payload["attributes"].KindCase.Should().Be(Value.KindOneofCase.StructValue);

        var fields = payload["attributes"].StructValue.Fields;
        fields["confidentiality"].StringValue.Should().Be("restricted");
        fields["department"].StringValue.Should().Be("legal");

        // フラットキー表現は書き込まない（過剰除外の原因になるため）。
        payload.Should().NotContainKey("attributes.confidentiality");
        payload.Should().NotContainKey("attributes.department");
    }

    // 属性が無い場合は attributes キー自体を書かない（RetrievalService 側は欠落を restricted へ倒す）。
    [Fact]
    public void BuildChunkPayload_WhenNoAttributes_OmitsAttributesKey()
    {
        var payload = Build();

        payload.Should().NotContainKey("attributes");
    }

    // FR-02: 基本メタデータ（document_id/title/text/markdown_uri/chunk_index）を保持する。
    [Fact]
    public void BuildChunkPayload_WritesCoreMetadata()
    {
        var payload = Build(tags: new List<string> { "a", "b" });

        payload["document_title"].StringValue.Should().Be("タイトル");
        payload["text"].StringValue.Should().Be("本文");
        payload["markdown_uri"].StringValue.Should().Be("s3://bucket/doc.md");
        payload["chunk_index"].IntegerValue.Should().Be(3);
        payload["tags"].ListValue.Values.Select(v => v.StringValue).Should().Equal("a", "b");
    }

    // FR-02, FR-03, SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 3:
    // **本文なしの点だけが `has_body` を持つ。** 既存の点はすべて本文チャンクなので、
    // キーの欠落が「本文あり」を正しく表す（backfill が要らない）。
    [Fact]
    public void BuildChunkPayload_OmitsHasBody_ForBodyChunks()
    {
        Build().Should().NotContainKey(DocumentBodyPresence.PayloadKey);
    }

    [Fact]
    public void BuildChunkPayload_WritesHasBodyFalse_ForMetadataPoint()
    {
        var payload = QdrantIngestionVectorStore.BuildChunkPayload(
            documentId: Guid.NewGuid(),
            title: "タイトル",
            text: MetadataIndexText.Build("タイトル", ["経理"]),
            chunkIndex: ChunkId.MetadataChunkIndex,
            markdownUri: "s3://bucket/doc.md",
            attributes: new Dictionary<string, string> { ["confidentiality"] = "internal" },
            tags: ["経理"],
            updatedAt: null,
            hasBody: false);

        payload[DocumentBodyPresence.PayloadKey].KindCase.Should().Be(Value.KindOneofCase.BoolValue);
        payload[DocumentBodyPresence.PayloadKey].BoolValue.Should().BeFalse();
        // 索引テキストは `text` に載る（全文索引をもう 1 対増やさない。[[IADR-0358]] 案 C）。
        payload["text"].StringValue.Should().Be("タイトル 経理");
        // FR-05: ABAC 属性はチャンクと同じネスト構造体で載る（判定軸を変えない）。
        payload["attributes"].StructValue.Fields.Should().ContainKey("confidentiality");
        // 索引を直接読んでも「本文チャンクではない」が分かる。
        payload["chunk_index"].IntegerValue.Should().Be(-1);
    }
}
