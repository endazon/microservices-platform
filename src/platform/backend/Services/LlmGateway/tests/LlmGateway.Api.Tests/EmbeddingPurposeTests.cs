using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using LlmGateway.Api.Composable.Adapters;
using LlmGateway.Api.Foundation.Routing;
using Microsoft.Extensions.Configuration;

namespace LlmGateway.Api.Tests;

// FR-02, FR-03, ADR-0016, ADR-0017 (#809): 埋め込みでクエリと文書を区別して送ることを固定する。
//
// 用途（EmbeddingRoutePurpose）は EmbeddingEndpoints が算出してルーターへ渡していたが、
// **プロバイダへは落ちていなかった**（IEmbeddingProvider に渡す口が無かった）。そのため:
//   - Ruri v3 が必須とする 1+3 プレフィクス（"検索クエリ: " / "検索文書: "）が付かない
//     （モデルカード実測: cl-nagoya/ruri-v3-310m の README「1+3 prefix scheme」）
//   - Voyage の input_type（query / document）が送られない
// この状態で nDCG@10 を測ると、**プレフィクス必須の Ruri がより大きく損をする**。
// ADR-0017 は「voyage-3.5 比で大幅に劣化するなら BGE-M3 へ切り替える」と定めているので、
// 欠陥を残したまま測ると切替の判断そのものを誤る（#336 の実測の前提条件）。
public class EmbeddingPurposeTests
{
    private const string EmbeddingJson = """{"data":[{"embedding":[0.1,0.2,0.3]}]}""";

    // 送信本文を捕捉するスタブ。プロバイダが「何を送ったか」を検査するために使う。
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EmbeddingJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // System.Text.Json は既定で非 ASCII を \uXXXX へエスケープするため、生の JSON 文字列を
    // 日本語で部分一致させると外れる。**解析して値で比べる**（表現ではなく内容を検査する）。
    private static string Field(string? body, string name)
    {
        body.Should().NotBeNull();
        var el = JsonDocument.Parse(body!).RootElement.GetProperty(name);
        // セルフホストは input が文字列、Voyage は配列（要素 1）で送る。
        return el.ValueKind == JsonValueKind.Array ? el[0].GetString()! : el.GetString()!;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build();

    // --- セルフホスト（Ruri v3） ------------------------------------------------

    // FR-02, ADR-0017 (#809): 索引用途では文書プレフィクスが前置される。
    [Fact]
    public async Task セルフホスト_索引用途では文書プレフィクスを前置する()
    {
        var handler = new CapturingHandler();
        var provider = new SelfHostedEmbeddingProvider(new SingleClientFactory(handler), Config(
            ("Embedding:SelfHosted:BaseUrl", "http://embedding-service:8080"),
            ("Embedding:SelfHosted:QueryPrefix", "検索クエリ: "),
            ("Embedding:SelfHosted:DocumentPrefix", "検索文書: ")));

        await provider.EmbedAsync("瑠璃色とは", "ruri-v3", 768, EmbeddingRoutePurpose.Index);

        Field(handler.Body, "input").Should().Be("検索文書: 瑠璃色とは");
    }

    // FR-03, ADR-0017 (#809): 検索用途ではクエリプレフィクスが前置される。
    [Fact]
    public async Task セルフホスト_検索用途ではクエリプレフィクスを前置する()
    {
        var handler = new CapturingHandler();
        var provider = new SelfHostedEmbeddingProvider(new SingleClientFactory(handler), Config(
            ("Embedding:SelfHosted:BaseUrl", "http://embedding-service:8080"),
            ("Embedding:SelfHosted:QueryPrefix", "検索クエリ: "),
            ("Embedding:SelfHosted:DocumentPrefix", "検索文書: ")));

        await provider.EmbedAsync("瑠璃色とは", "ruri-v3", 768, EmbeddingRoutePurpose.Query);

        Field(handler.Body, "input").Should().Be("検索クエリ: 瑠璃色とは");
    }

    // ADR-0017 (#809): プレフィクスは**モデル固有**であり、既定は空。
    // ADR-0017 が劣化時の代替に挙げる BGE-M3 はこのプレフィクスを使わないため、
    // 未設定なら現行と同じ「素の text」を送る（付けっぱなしは別の意味で埋め込みを歪める）。
    [Fact]
    public async Task セルフホスト_プレフィクス未設定なら素の本文を送る()
    {
        var handler = new CapturingHandler();
        var provider = new SelfHostedEmbeddingProvider(new SingleClientFactory(handler), Config(
            ("Embedding:SelfHosted:BaseUrl", "http://embedding-service:8080")));

        await provider.EmbedAsync("瑠璃色とは", "bge-m3", 1024, EmbeddingRoutePurpose.Index);

        Field(handler.Body, "input").Should().Be("瑠璃色とは", "プレフィクス未設定なら素の本文を送る");
    }

    // --- Voyage -----------------------------------------------------------------

    // FR-02, ADR-0016 (#809): 索引用途は input_type=document で送る。
    [Fact]
    public async Task Voyage_索引用途では_input_type_が_document()
    {
        var handler = new CapturingHandler();
        var provider = new VoyageEmbeddingProvider(new SingleClientFactory(handler), Config(
            ("Embedding:Voyage:ApiKey", "test-key")));

        await provider.EmbedAsync("瑠璃色とは", "voyage-3.5", 1024, EmbeddingRoutePurpose.Index);

        handler.Body.Should().Contain("\"input_type\":\"document\"");
    }

    // FR-03, ADR-0016 (#809): 検索用途は input_type=query で送る。
    [Fact]
    public async Task Voyage_検索用途では_input_type_が_query()
    {
        var handler = new CapturingHandler();
        var provider = new VoyageEmbeddingProvider(new SingleClientFactory(handler), Config(
            ("Embedding:Voyage:ApiKey", "test-key")));

        await provider.EmbedAsync("瑠璃色とは", "voyage-3.5", 1024, EmbeddingRoutePurpose.Query);

        handler.Body.Should().Contain("\"input_type\":\"query\"");
    }

    // FR-02, FR-03 (#809): 同じ本文でも用途が違えば送信内容が変わる。
    // 「用途がプロバイダまで届いている」ことを、両プロバイダについて 1 本で固定する。
    [Fact]
    public async Task 同じ本文でも用途が違えば送信内容が変わる()
    {
        var selfHostedConfig = Config(
            ("Embedding:SelfHosted:BaseUrl", "http://embedding-service:8080"),
            ("Embedding:SelfHosted:QueryPrefix", "検索クエリ: "),
            ("Embedding:SelfHosted:DocumentPrefix", "検索文書: "));

        var h1 = new CapturingHandler();
        await new SelfHostedEmbeddingProvider(new SingleClientFactory(h1), selfHostedConfig)
            .EmbedAsync("同じ本文", "ruri-v3", 768, EmbeddingRoutePurpose.Query);

        var h2 = new CapturingHandler();
        await new SelfHostedEmbeddingProvider(new SingleClientFactory(h2), selfHostedConfig)
            .EmbedAsync("同じ本文", "ruri-v3", 768, EmbeddingRoutePurpose.Index);

        h1.Body.Should().NotBe(h2.Body, "用途を無視すると両者が同一になる（本 issue の欠陥そのもの）");

        var voyageConfig = Config(("Embedding:Voyage:ApiKey", "test-key"));

        var h3 = new CapturingHandler();
        await new VoyageEmbeddingProvider(new SingleClientFactory(h3), voyageConfig)
            .EmbedAsync("同じ本文", "voyage-3.5", 1024, EmbeddingRoutePurpose.Query);

        var h4 = new CapturingHandler();
        await new VoyageEmbeddingProvider(new SingleClientFactory(h4), voyageConfig)
            .EmbedAsync("同じ本文", "voyage-3.5", 1024, EmbeddingRoutePurpose.Index);

        h3.Body.Should().NotBe(h4.Body, "用途を無視すると両者が同一になる（本 issue の欠陥そのもの）");
    }
}
