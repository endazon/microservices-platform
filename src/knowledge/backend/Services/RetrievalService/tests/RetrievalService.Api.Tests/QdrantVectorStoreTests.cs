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
}
