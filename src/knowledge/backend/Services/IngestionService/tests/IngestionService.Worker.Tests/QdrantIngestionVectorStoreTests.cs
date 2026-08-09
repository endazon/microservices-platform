using IngestionService.Worker.Composable.Adapters;
using FluentAssertions;
using IngestionService.Worker.Foundation.Ports;
using IngestionService.Worker.Foundation.Domain;
using Qdrant.Client.Grpc;

namespace IngestionService.Worker.Tests;

// FR-05, IADR-0014（選択肢C・実機検証済み・Issue #71）: 取り込み時に書き込む Qdrant ペイロードで
// ABAC 属性がネスト構造体 `attributes -> { k: v }` として構築されることを検証する。
// フラットキー `attributes.{k}` で書き込むと、フィルタ側がドットを JSON パスとして解釈するため
// 過剰除外が発生する（実機検証で確認）。書き込み表現を RetrievalService.QdrantVectorStore と一致させる。
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
}
