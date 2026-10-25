using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.Search.RemoveDeleted;
using RetrievalService.Infrastructure.ExternalServices;

namespace RetrievalService.Tests.Features.Search.RemoveDeleted;

// 🔴 FR-06, FR-19, ADR-0057 決定 1 (#2137): **決定的ローカル埋め込みの検証スタックでも、削除後に全コレクションから点が消える。**
//
// そのスタックでは主（`Qdrant:CollectionName`）を決定的なコレクションへ差し替える（helm の embedding.deterministicLocal）。
// 取り込みは `Embedding:Collections` の全コレクション（voyage・決定的）へ書くので、検索が読む集合（主・束ねる追加・語彙索引）だけを
// 消すと voyage に点が残る。削除の対象を取り込みと同じ構成（`Embedding:Collections`）から導いて、その差を消す。
[Trait("TestKind", "Unit")]
public class WrittenCollectionsDeletionTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private const string Voyage = "knowledge_chunks_voyage_3_5";
    private const string Deterministic = "knowledge_chunks_deterministic_v1";
    private const string Lexical = "knowledge_chunks_lexical";

    private static readonly Guid DocA = Guid.Parse("21370000-0000-0000-0000-00000000000a");
    private static readonly Guid DocB = Guid.Parse("21370000-0000-0000-0000-00000000000b");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static ChunkPayload Chunk(Guid docId) => new(
        Guid.NewGuid(), docId, $"doc-{docId:N}", "規程の本文", [0.1f, 0.2f], $"storage://b/{docId:N}.md",
        new Dictionary<string, string> { ["confidentiality"] = "internal" }, [], DateTimeOffset.UtcNow);

    // 削除だけの対象は「取り込みが書く集合 − 検索が読む集合」。空の名前・重複は捨てる。
    [Fact]
    public void 削除だけの対象は取り込みが書くが検索が読まないコレクションである()
    {
        var config = Config(
            ("Embedding:Collections:0:Name", Voyage),
            ("Embedding:Collections:1:Name", ""), // compose の既定 `${…:-}`（名前が空）
            ("Embedding:Collections:2:Name", Deterministic),
            ("Embedding:Collections:3:Name", Voyage));

        QdrantVectorStore.ResolveWrittenOnlyCollectionNames(config, [Deterministic, Lexical])
            .Should().Equal([Voyage], "主を決定的なコレクションへ差し替えたので、voyage だけが読まれない");
        QdrantVectorStore.ResolveWrittenOnlyCollectionNames(config, [Voyage, Deterministic, Lexical])
            .Should().BeEmpty("陽性対照: 読む集合が書く集合を覆えば差は空（既定の配備）");
    }

    // 本番の構成（本サービスの appsettings.json）＋ helm が決定的モードで描く値。既定の配備では差は空。
    [Fact]
    public void 本サービスの構成では決定的モードのときだけvoyageが削除だけの対象になる()
    {
        var baseConfig = factory.Services.GetRequiredService<IConfiguration>();
        Read(baseConfig).Should().BeEmpty("既定の配備（主＝voyage）では削除は従来と同一");

        using var det = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Qdrant:CollectionName"] = Deterministic,
            })));
        Read(det.Services.GetRequiredService<IConfiguration>())
            .Should().Equal([Voyage], "取り込みと同じ構成（Embedding:Collections）から voyage が導かれる");

        static IReadOnlyList<string> Read(IConfiguration c) =>
            QdrantVectorStore.ResolveWrittenOnlyCollectionNames(c,
            [
                QdrantVectorStore.ResolveCollectionName(c), .. QdrantVectorStore.ResolveFusedCollectionNames(c),
                QdrantVectorStore.ResolveLexicalCollectionName(c),
            ]);
    }

    // 決定的モードで、主（決定的）・語彙索引・削除だけの対象（voyage）のすべてから削除した文書の点が消える。他の文書は残る。
    [Fact]
    public async Task 決定的モードで削除後に全コレクションから点が消える()
    {
        var deterministic = new InMemoryVectorStore();
        var lexical = new InMemoryVectorStore();
        var voyage = new InMemoryVectorStore();
        foreach (var store in new[] { deterministic, lexical, voyage })
        {
            await store.UpsertAsync(Chunk(DocA), Ct);
            await store.UpsertAsync(Chunk(DocB), Ct);
        }
        var consumer = new DocumentDeletedConsumer(deterministic,
            new FusedCollections([new FusedCollection(Lexical, lexical, NoQueryEmbedding.Instance, LexicalOnly: true)]),
            new WrittenCollections([new WrittenCollection(Voyage, voyage)]),
            ConsumerTimeoutsForTests.Calls(), DocumentDeletedTimeouts.Default, NullLogger<DocumentDeletedConsumer>.Instance);

        await consumer.Handle(new DocumentDeleted(DocA, DateTimeOffset.UtcNow), Ct);

        foreach (var (name, store) in new[] { (Deterministic, deterministic), (Lexical, lexical), (Voyage, voyage) })
        {
            var left = await store.KeywordSearchAsync("規程", 10, null, Ct);
            left.Should().NotContain(r => r.DocumentId == DocA, $"{name} に削除した文書の点が残らない");
            left.Should().Contain(r => r.DocumentId == DocB, $"陽性対照: {name} の他の文書は残る");
        }
    }
}
