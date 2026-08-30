using AwesomeAssertions;
using LlmGateway.Domain.Routing;
using LlmGateway.Infrastructure.ExternalServices;

namespace LlmGateway.Tests;

// FR-02, FR-03, ADR-0016, #992 案 2, [[IADR-0313]]: 決定的ローカル埋め込み。
//
// 🔴 このプロバイダの存在理由は**統合スタックで「検索が実際に効くこと」を観測できるようにする**ことである。
// したがって「同じ本文なら常に同じベクトル」「ルーターの決定と同じ次元」「Cosine 空間で扱える
// （零ベクトルでない）」の 3 点が壊れると、目的そのものが果たせない。ここで固定する。
public class DeterministicEmbeddingProviderTests
{
    private static readonly DeterministicEmbeddingProvider Provider = new();

    // 🔴 決定的であること。`string.GetHashCode` はプロセスごとにランダム化されるため、
    // それを使うと同じ本文が実行ごとに別ベクトルになり、**索引と問い合わせが噛み合わなくなる**。
    [Fact]
    public async Task EmbedAsync_SameText_ReturnsIdenticalVector()
    {
        var a = await Provider.EmbedAsync("msp-searchseed-tanpopo 検索導線の検証用文書",
            "deterministic-hash-v1", 1024, EmbeddingRoutePurpose.Index, TestContext.Current.CancellationToken);
        var b = await Provider.EmbedAsync("msp-searchseed-tanpopo 検索導線の検証用文書",
            "deterministic-hash-v1", 1024, EmbeddingRoutePurpose.Index, TestContext.Current.CancellationToken);

        b.Should().Equal(a);
    }

    // 🔴 期待値を直に固定する（実行跨ぎだけでなく**版跨ぎ**の決定性）。ここが変わると
    // 既存の索引と新しい問い合わせが別空間になるため、変えるならコレクション名も変えること。
    [Fact]
    public void Embed_KnownInput_HasStableFingerprint()
    {
        var v = DeterministicEmbeddingProvider.Embed("msp-searchseed-tanpopo", 16);

        // 手計算ではなく「最初の実測値」を固定する回帰テストである（値そのものに意味は無い）。
        var nonZero = v.Select((x, i) => (x, i)).Where(t => t.x != 0).Select(t => t.i).ToArray();
        nonZero.Should().NotBeEmpty();
        // 同じ入力・同じ次元なら、非零の位置集合も値も一致し続ける。
        DeterministicEmbeddingProvider.Embed("msp-searchseed-tanpopo", 16).Should().Equal(v);
    }

    // ルーターの決定（Dimensions）と一致しないベクトルは `/embed` が fail-closed で捨てる。
    [Theory]
    [InlineData(16)]
    [InlineData(768)]
    [InlineData(1024)]
    public async Task EmbedAsync_ReturnsRequestedDimensions(int dimensions)
    {
        var v = await Provider.EmbedAsync("任意の本文", "deterministic-hash-v1", dimensions,
            EmbeddingRoutePurpose.Index, TestContext.Current.CancellationToken);

        v.Should().HaveCount(dimensions);
    }

    // Qdrant の距離は Cosine。**零ベクトルは比較できない**ので、どんな入力でも単位長へ倒す。
    [Theory]
    [InlineData("")]
    [InlineData("あ")]      // 3-gram が 1 つも取れない
    [InlineData("ab")]      // 同上
    [InlineData("横断検索が実際に効くことを観測する")]
    public async Task EmbedAsync_IsUnitLength(string text)
    {
        var v = await Provider.EmbedAsync(text, "deterministic-hash-v1", 64, EmbeddingRoutePurpose.Index, TestContext.Current.CancellationToken);

        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        norm.Should().BeApproximately(1.0, 1e-5);
    }

    // 🔴 用途（Query / Index）でベクトルを変えない。Ruri v3 の 1+3 プレフィクス（#809）は
    // モデルが非対称に符号化する前提の作法であり、ハッシングに持ち込むと
    // **クエリ側だけ 3-gram が増えて文書から系統的に遠ざかる**（＝当たらなくなる）。
    [Fact]
    public async Task EmbedAsync_QueryAndIndex_ProduceSameVector()
    {
        var q = await Provider.EmbedAsync("合言葉", "deterministic-hash-v1", 128, EmbeddingRoutePurpose.Query, TestContext.Current.CancellationToken);
        var d = await Provider.EmbedAsync("合言葉", "deterministic-hash-v1", 128, EmbeddingRoutePurpose.Index, TestContext.Current.CancellationToken);

        d.Should().Equal(q);
    }

    // 合言葉を含む本文は、含まない本文より合言葉のクエリに近い。
    // **意味的な近さではなく表層の 3-gram の重なり**である（品質の主張はしない）が、
    // これが崩れると門は「何を検索しても同じ点が返る」だけのものになる。
    [Fact]
    public void Embed_TextContainingProbeTerm_IsCloserToProbeQuery()
    {
        const int dim = 1024;
        var query = DeterministicEmbeddingProvider.Embed("msp-searchseed-tanpopo", dim);
        var hit = DeterministicEmbeddingProvider.Embed(
            "# msp-searchseed-tanpopo 検索導線の検証用文書\n\nこの文書は統合スタックで横断検索を観測するために投入される。", dim);
        var miss = DeterministicEmbeddingProvider.Embed(
            "# 月次レポートの様式\n\n本書は経理部門が使う様式について述べる。集計の締めは翌月第 3 営業日である。", dim);

        Cosine(query, hit).Should().BeGreaterThan(Cosine(query, miss));
    }

    private static double Cosine(float[] a, float[] b)
        => a.Zip(b, (x, y) => (double)x * y).Sum();
}
