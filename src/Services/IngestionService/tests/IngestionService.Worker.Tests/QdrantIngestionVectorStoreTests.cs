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
        Dictionary<string, string>? attributes = null, List<string>? tags = null) =>
        QdrantIngestionVectorStore.BuildChunkPayload(
            documentId: Guid.NewGuid(),
            title: "タイトル",
            text: "本文",
            chunkIndex: 3,
            markdownUri: "s3://bucket/doc.md",
            attributes: attributes ?? new Dictionary<string, string>(),
            tags: tags ?? new List<string>());

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
