using FluentAssertions;
using Qdrant.Client.Grpc;
using RetrievalService.Api.Infrastructure;

namespace RetrievalService.Api.Tests;

// FR-05, FR-11 (Issue #58 の #2): Qdrant ペイロードから ABAC 属性を DTO へ復元することを検証する。
// 復元しないと機密区分判定が常に「属性欠落 → restricted」へ縮退し、FR-11 の機密区分別ルーティングが
// 事実上無効化される。書き込み表現（フラットキー）とネスト構造体表現の双方に対応する。
public class QdrantVectorStoreTests
{
    // (a) フラットキー表現: 書き込み（UpsertAsync）と同じ `attributes.{k}` から復元できる。
    [Fact]
    public void ExtractAttributes_RestoresFromFlatKeys()
    {
        var payload = new Dictionary<string, Value>
        {
            ["document_id"] = new() { StringValue = "doc" },
            ["text"] = new() { StringValue = "本文" },
            ["attributes.confidentiality"] = new() { StringValue = "confidential" },
            ["attributes.department"] = new() { StringValue = "sales" },
        };

        var attrs = QdrantVectorStore.ExtractAttributes(payload);

        attrs.Should().HaveCount(2);
        attrs["confidentiality"].Should().Be("confidential");
        attrs["department"].Should().Be("sales");
    }

    // (b) ネスト構造体表現: Qdrant がドットをネストパスとして格納する場合でも復元できる。
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

    // フラットキーが存在する場合はそれを尊重し、ネスト側では上書きしない。
    [Fact]
    public void ExtractAttributes_PrefersFlatKeyOverNested()
    {
        var nested = new Value { StructValue = new Struct() };
        nested.StructValue.Fields["confidentiality"] = new Value { StringValue = "restricted" };

        var payload = new Dictionary<string, Value>
        {
            ["attributes.confidentiality"] = new() { StringValue = "internal" },
            ["attributes"] = nested,
        };

        QdrantVectorStore.ExtractAttributes(payload)["confidentiality"].Should().Be("internal");
    }
}
