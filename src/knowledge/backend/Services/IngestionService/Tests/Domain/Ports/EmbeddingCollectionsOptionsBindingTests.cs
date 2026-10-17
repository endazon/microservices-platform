using System.Runtime.CompilerServices;
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
    // 2 件の構成（opt-in の Ruri を有効にした形）。JSON の配列は `:0:` `:1:` の添字キーへ平坦化される。
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
        options!.Collections.Should().HaveCount(2, "構成は voyage / ruri の 2 コレクションを宣言している");
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

    // FR-02, ADR-0127 決定 5, IADR-0085 (#1746 段 S4): **既定の appsettings.json は Ruri のコレクションを宣言しない。**
    // セルフホストの配備物は opt-in のまま残る（ADR-0127 決定 5）が、無効のままの配備でも空の Ruri のコレクションが
    // 作られ、索引が張られ、削除のたびに 1 回ずつ呼ばれていた。Ruri は opt-in のとき（Helm `embedding.enabled`・
    // compose `SEARCH_FUSED_COLLECTION`）に添字 1 へ足される。
    [Fact]
    public void 既定のappsettingsはvoyageだけを宣言しRuriを宣言しない()
    {
        var options = new ConfigurationBuilder()
            .AddJsonFile(SourceAppsettings(), optional: false)
            .Build()
            .GetSection(EmbeddingCollectionsOptions.SectionName)
            .Get<EmbeddingCollectionsOptions>();

        options!.Collections.Select(c => c.Name).Should().Equal(["knowledge_chunks_voyage_3_5"],
            "Ruri のコレクションは opt-in のときだけ添字 1 に足される（既定で作ると空のまま残る）");
    }

    // FR-02, IADR-0085, IADR-0313 (#1746 段 S4): **添字が欠けても束ねられる。** 決定的ローカル埋め込み（検証スタック）は
    // 添字 2 に足し、opt-in の Ruri は添字 1 を使う。Ruri を有効にしない検証スタックでは 0 と 2 だけになる。
    [Fact]
    public void 添字が欠けた構成でも全件を束ねる()
    {
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Embedding:Collections:0:Name"] = "knowledge_chunks_voyage_3_5",
                ["Embedding:Collections:0:VectorSize"] = "1024",
                ["Embedding:Collections:2:Name"] = "knowledge_chunks_deterministic_v1",
                ["Embedding:Collections:2:VectorSize"] = "1024",
            })
            .Build()
            .GetSection(EmbeddingCollectionsOptions.SectionName)
            .Get<EmbeddingCollectionsOptions>()!
            .DropUnnamed();

        options.Collections.Select(c => c.Name).Should()
            .Equal(["knowledge_chunks_voyage_3_5", "knowledge_chunks_deterministic_v1"]);
    }

    // FR-02, IADR-0085 (#1746 段 S4): compose は opt-in が無効のとき `${SEARCH_FUSED_COLLECTION:-}` の空文字を
    // 添字 1 の名前として渡す。**名前が空の定義は捨てる**（捨てないと名前の無いコレクションを作ろうとして
    // ブートストラップが失敗する）。名前のある定義と順序は保つ。
    [Fact]
    public void 名前が空の定義は捨てる()
    {
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Embedding:Collections:0:Name"] = "knowledge_chunks_voyage_3_5",
                ["Embedding:Collections:0:VectorSize"] = "1024",
                ["Embedding:Collections:1:Name"] = "",
                ["Embedding:Collections:1:VectorSize"] = "768",
                ["Embedding:Collections:2:Name"] = " ",
            })
            .Build()
            .GetSection(EmbeddingCollectionsOptions.SectionName)
            .Get<EmbeddingCollectionsOptions>()!;

        options.Collections.Should().HaveCount(3, "陽性対照: 空の名前も一度は束ねられて届く");
        options.DropUnnamed().Collections.Select(c => c.Name).Should().Equal(["knowledge_chunks_voyage_3_5"]);
    }

    private static string SourceAppsettings([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "appsettings.json"));

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
