using System.Text.Json;
using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;

namespace Knowledge.IntegrationTests.Search;

// 🔴 FR-06, ADR-0057 決定 1 (#2137): **取り込みと検索の `Embedding:Collections`（モデル別コレクションの既定の一覧）は同じ値である。**
//
// 取り込みはこの一覧のコレクションへ書き、検索は同じ一覧から「書くが読まないコレクション」を導いて文書の削除の対象に加える
// （`RetrievalService.Domain.WrittenCollections`）。サービスを跨ぐので型では束ねられない。片方だけ変えると、削除の対象が
// 書き込み先と食い違い、削除した文書の点がそのコレクションに残る。helm・compose が足す添字（Ruri・決定的ローカル）は
// 検索の主・束ねる追加と同じ値から描かれるので、ここでは既定（appsettings.json）の一致を固定する。
// Docker を要さない（PR の CI で走る）。
[Trait("TestKind", "Integration")]
public sealed class EmbeddingCollectionsParityTests
{
    private static string Collections(string relative)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile.Find(relative)));
        return doc.RootElement.GetProperty("Embedding").GetProperty("Collections").GetRawText();
    }

    [Fact]
    public void 取り込みと検索のEmbeddingCollectionsは同じ値である()
    {
        var ingestion = Collections("src/knowledge/backend/Services/IngestionService/appsettings.json");
        var retrieval = Collections("src/knowledge/backend/Services/RetrievalService/appsettings.json");

        JsonDocument.Parse(retrieval).RootElement.EnumerateArray().Should().NotBeEmpty("前提: 一覧がある");
        Normalize(retrieval).Should().Be(Normalize(ingestion));
    }

    private static string Normalize(string json) =>
        JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);
}
