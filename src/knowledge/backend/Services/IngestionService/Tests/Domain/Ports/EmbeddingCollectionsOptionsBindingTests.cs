using AwesomeAssertions;
using IngestionService.Domain.Ports;
using Microsoft.Extensions.Configuration;

namespace IngestionService.Tests.Domain.Ports;

// FR-02, ADR-0016 (#806): `EmbeddingCollectionsOptions` が **設定から実際に読めること**を固定する。
//
// 既存の QdrantIngestionVectorStoreTests は options を直接組み立てているため、
// `SectionName` の誤りを 1 件も踏まなかった。実際 `SectionName = "Embedding:Collections"` と
// プロパティ `Collections` の組み合わせは `Embedding:Collections:Collections` を探しにいき、
// **存在しないので空リストのままバインドが成功**していた（例外は出ない）。
// 結果 `EnsureCollectionsAsync` の foreach が 0 回まわり、コレクションが 1 つも作られないのに
// 「ensured」の成功ログだけが出る —— 稼働クラスタで `GET /collections` が空を返すことで実測した。
//
// したがってここで検査するのは「バインド結果が空でないこと」であって、値の中身ではない。
[Trait("TestKind", "Unit")]
public class EmbeddingCollectionsOptionsBindingTests
{
    // 実際の appsettings.json と同じ形。JSON の配列は `:0:` `:1:` の添字キーへ平坦化される。
    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Embedding:Collections:0:Name"] = "knowledge_chunks_voyage_3_5",
                ["Embedding:Collections:0:VectorSize"] = "1024",
                ["Embedding:Collections:1:Name"] = "knowledge_chunks_ruri_v3",
                ["Embedding:Collections:1:VectorSize"] = "768",
            })
            .Build();

    // FR-02, ADR-0016 (#806): SectionName でバインドするとモデル別コレクションが 2 件読めること。
    [Fact]
    public void SectionName_で束ねると設定のコレクションが読める()
    {
        var options = BuildConfiguration()
            .GetSection(EmbeddingCollectionsOptions.SectionName)
            .Get<EmbeddingCollectionsOptions>();

        options.Should().NotBeNull();
        options!.Collections.Should().HaveCount(2, "appsettings.json は voyage / ruri の 2 コレクションを宣言している");
        options.Collections.Select(c => c.Name).Should()
            .BeEquivalentTo("knowledge_chunks_voyage_3_5", "knowledge_chunks_ruri_v3");
        options.Collections.Single(c => c.Name == "knowledge_chunks_voyage_3_5").VectorSize.Should().Be(1024);
        options.Collections.Single(c => c.Name == "knowledge_chunks_ruri_v3").VectorSize.Should().Be(768);
    }

    // FR-02, ADR-0016 (#806): 空バインドが「成功」してしまう形を、症状の側から固定する。
    // 索引が 1 つも作られないのに成功ログが出る状態は、ここが空になることと同値である。
    [Fact]
    public void バインド結果が空になってはいけない()
    {
        var options = BuildConfiguration()
            .GetSection(EmbeddingCollectionsOptions.SectionName)
            .Get<EmbeddingCollectionsOptions>();

        options!.Collections.Should().NotBeEmpty(
            "空だと EnsureCollectionsAsync の foreach が 0 回まわり、"
            + "コレクション未作成のまま成功ログが出る。さらに DeleteByDocumentFromAllAsync も "
            + "無言の no-op になり、機密区分の引き上げ時に旧コレクションへチャンクが残る");
    }

    // FR-02, ADR-0016 (#806): セクション名の末尾とプロパティ名が重複していないこと。
    // これが本バグの型そのものである（`Embedding:Collections` ＋ プロパティ `Collections`）。
    // 隣の EmbeddingRoutingOptions（`Embedding:Routing` ＋ `Endpoints`）は重複していない。
    [Fact]
    public void セクション名の末尾がプロパティ名と重複していない()
    {
        var last = EmbeddingCollectionsOptions.SectionName.Split(':')[^1];

        last.Should().NotBe(nameof(EmbeddingCollectionsOptions.Collections),
            "セクション名の末尾がプロパティ名と同じだと、バインダは一段深い "
            + $"`{EmbeddingCollectionsOptions.SectionName}:{nameof(EmbeddingCollectionsOptions.Collections)}` "
            + "を探しにいき、存在しないので空のままバインドが成功する");
    }
}
