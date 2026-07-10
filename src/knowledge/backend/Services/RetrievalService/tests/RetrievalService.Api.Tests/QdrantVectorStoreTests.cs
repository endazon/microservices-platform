using RetrievalService.Api.Composable.Adapters;
using FluentAssertions;
using Qdrant.Client.Grpc;

namespace RetrievalService.Api.Tests;

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
}
