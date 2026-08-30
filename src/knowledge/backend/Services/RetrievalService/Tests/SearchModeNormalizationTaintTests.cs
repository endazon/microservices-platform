using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Logging;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.Search.Hybrid;

namespace RetrievalService.Tests;

// NFR, FR-03, CodeQL(cs/log-forging) アラート #24 (#1019):
// **正規化は許可リスト側の定数を返す。利用者入力から作った文字列を返さない。**
//
// 従前の実装は `IsValid(mode) ? mode!.ToLowerInvariant() : Hybrid` だった。値は定数と一致するが、
// 返る**実体は利用者入力由来**であり、テイントが `HybridSearchService` のログまで伝播していた。
// #1019 は「値域が閉じているので偽陽性」と論じたが、**値域が閉じていることと、実体が
// 利用者入力由来でないことは別の主張**である。CodeQL が追っていたのは後者で、そちらは真だった。
//
// 直し方は sink の sanitize ではなく**発生源で断つ**こと。先例は `LlmRouter.ResolveModel`。
public class SearchModeNormalizationTaintTests
{
    // 実際に書かれたログ行を捕まえる。整形済みの本文を見るのが要点である。
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    // `/embed` の縮退（200 ＋ 空ベクトル）を再現する。この経路だけが mode をログへ載せる。
    private sealed class EmptyVectorEmbedding : IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<float>());
    }

    private sealed class EmptyVectorStore : IVectorStore
    {
        public Task<List<SearchResultDto>> SearchAsync(
            float[] queryVector, int topK, ScopeFilter? filters, CancellationToken ct = default)
            => Task.FromResult(new List<SearchResultDto>());

        public Task<List<SearchResultDto>> KeywordSearchAsync(
            string query, int topK, ScopeFilter? filters, CancellationToken ct = default)
            => Task.FromResult(new List<SearchResultDto>());

        public Task<List<SearchResultDto>> SearchWithinDocumentsAsync(
            float[] queryVector, int topK, IReadOnlyCollection<Guid> documentIds,
            ScopeFilter? filters, CancellationToken ct = default)
            => Task.FromResult(new List<SearchResultDto>());

        public Task<List<string>> ListAttributeValuesAsync(
            string payloadKey, ScopeFilter? filters, CancellationToken ct = default)
            => Task.FromResult<List<string>>([]);

        public Task UpsertAsync(ChunkPayload chunk, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static readonly AccessScope Granted = new([], true);

    // 🔴 **核心。** 正規化が返すのは許可リストの**定数そのもの**であり、入力から作った
    // 新しい文字列ではない。`ToLowerInvariant()` を返す実装では
    // `"HYBRID".ToLowerInvariant()` が新しい実体を割り当てるので、この表明が落ちる。
    [Theory]
    [InlineData("HYBRID")]
    [InlineData("Hybrid")]
    [InlineData("hybrid")]
    public void モード正規化は許可リストの定数の実体を返す(string input)
    {
        ReferenceEquals(SearchModes.Normalize(input), SearchModes.Hybrid)
            .Should().BeTrue("利用者入力から作った文字列を下流（ログ）へ持ち込まない");
    }

    // 🔴 **期待値の定数を InlineData 経由で渡さない。** xUnit はデータを直列化して復元するため、
    // 引数として届く文字列は定数とは別の実体になり、ReferenceEquals が実装と無関係に落ちる
    // （実測済み）。定数はテスト本文から直接参照する。
    [Fact]
    public void モード正規化はどの値でも定数の実体を返す()
    {
        ReferenceEquals(SearchModes.Normalize("KEYWORD"), SearchModes.Keyword).Should().BeTrue();
        ReferenceEquals(SearchModes.Normalize("Semantic"), SearchModes.Semantic).Should().BeTrue();
        ReferenceEquals(SearchModes.Normalize("HYBRID"), SearchModes.Hybrid).Should().BeTrue();
    }

    // 未知・null の縮退先も定数の実体である。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fuzzy")]
    [InlineData("hybrid\nINFO 偽の行")]
    public void 未知の値は既定の定数の実体へ縮退する(string? input)
    {
        ReferenceEquals(SearchModes.Normalize(input), SearchModes.Hybrid).Should().BeTrue();
    }

    // 並び順も同一構造なので同じ性質を固定する（定数は本文から直接参照する。上の注記と同じ理由）。
    [Fact]
    public void 並び順正規化も定数の実体を返す()
    {
        ReferenceEquals(SearchSorts.Normalize("UPDATED"), SearchSorts.Updated).Should().BeTrue();
        ReferenceEquals(SearchSorts.Normalize("Relevance"), SearchSorts.Relevance).Should().BeTrue();
        ReferenceEquals(SearchSorts.Normalize("nope"), SearchSorts.Relevance).Should().BeTrue();
        ReferenceEquals(SearchSorts.Normalize(null), SearchSorts.Relevance).Should().BeTrue();
    }

    // 🔴 陽性対照 1: **観測可能な値は従来どおりである。**
    // これが無いと「常に Hybrid を返す」実装でも上の表明が全部緑になる。
    [Theory]
    [InlineData("KEYWORD", "keyword")]
    [InlineData("Semantic", "semantic")]
    [InlineData("HyBrId", "hybrid")]
    [InlineData("fuzzy", "hybrid")]
    [InlineData(null, "hybrid")]
    public void 正規化の値は従来と変わらない(string? input, string expected)
    {
        SearchModes.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("UPDATED", "updated")]
    [InlineData("Relevance", "relevance")]
    [InlineData("nope", "relevance")]
    public void 並び順の値も従来と変わらない(string input, string expected)
    {
        SearchSorts.Normalize(input).Should().Be(expected);
    }

    // 🔴 陽性対照 2: **モードは実際のログ行に現れる。**
    // 「ログへ書かない」実装にすれば CodeQL は黙るが、#995 が塞いだ穴
    //（静かな縮退が応答から区別できない）が開き直る。ログ行に出続けることを固定する。
    [Theory]
    [InlineData(SearchModes.Semantic)]
    [InlineData(SearchModes.Hybrid)]
    public async Task 埋め込み不能の警告にモードが載り一行に収まる(string mode)
    {
        var logger = new CapturingLogger<HybridSearchService>();
        var svc = new HybridSearchService(
            new EmptyVectorStore(), new EmptyVectorEmbedding(), logger);

        await svc.SearchAsync(
            new SearchRequest("q", 10, null, Granted, mode), TestContext.Current.CancellationToken);

        var message = logger.Messages.Should().ContainSingle().Which;
        message.Should().Contain(mode);
        message.Should().NotContain("\n").And.NotContain("\r");
    }
}
