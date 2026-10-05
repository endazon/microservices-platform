using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Common.Observability;
using RetrievalService.Domain.Ports;
using RetrievalService.Features.Search.Hybrid;
using RetrievalService.Tests.Features.Search.Hybrid;

namespace RetrievalService.Tests.Features.Search;

// FR-03, FR-04, FR-05, SC-02, ADR-0127 決定 3, ADR-0018, [[IADR-0498]] 決定 1・11 (#1746 段 S2):
// **合成点（`Program.cs`）と `/search` の端点を通した再順位付け。**
//
//   - 既定（`Rerank:Enabled` 未設定）では段の型を登録しない（着脱可能な段。結果は段を足す前と同じ）
//   - 有効にすると段が登録され、AI 分析が RAG のために送る形（TopK 5・モード／並び指定なし）の検索にも掛かる
//   - 段へ渡るのは**索引の側で ABAC が落とした後**の候補だけ（権限外の文書の本文はプロンプトに無い）
[Trait("TestKind", "Integration")]
public class ClaudeRerankWiringTests
{
    // 有効にした器。ゲートウェイの輸送だけを記録する偽物へ差し替える（本番の段・本番の出口を通す）。
    private sealed class RerankEnabledFactory(FakeRerankClient client) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // 🔴 **`UseSetting` で渡す。** 段の有無は `Program.cs` が `builder.Build()` の前に読むので、
            // `ConfigureAppConfiguration` では間に合わない（二段検索の試験と同じ作法）。
            builder.UseSetting("Rerank:Enabled", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRerankCompletionClient>();
                services.AddSingleton<IRerankCompletionClient>(client);
            });
        }
    }

    private static ChunkPayload Chunk(string text, string dept) =>
        new(Guid.NewGuid(), Guid.NewGuid(), $"doc:{text}", text, new float[1536], "s3://bucket/x.md",
            new() { ["dept"] = dept, [ConfidentialityLevels.AttributeKey] = ConfidentialityLevels.Internal }, []);

    private static async Task SeedAsync(TestWebApplicationFactory factory, params ChunkPayload[] chunks)
    {
        var store = factory.Services.GetRequiredService<IVectorStore>();
        foreach (var c in chunks)
            await store.UpsertAsync(c);
    }

    // AI 分析（`HttpRagSearchTransport`）が送るのと同じ形。
    private static SearchRequest RagShaped(string query) =>
        new(query, 5, null, new AccessScope([new AttributeFilter("dept", ["sales"])], GrantsAccess: true));

    // T-108 (ADR-0018・[[IADR-0498]] 決定 11): 既定では段の型が DI に無い（構成だけで素の検索に戻る）。
    [Fact]
    public async Task 既定では段を登録しない()
    {
        await using var factory = new TestWebApplicationFactory();

        factory.Services.GetService<ISearchReranker>().Should().BeNull();
        factory.Services.GetService<IRerankCompletionClient>().Should().BeNull();
        factory.Services.GetRequiredService<SearchRerankOptions>().Enabled.Should().BeFalse();
        factory.Services.GetService<RerankMetrics>().Should().NotBeNull("縮退の計器は無効でも登録する");
    }

    // T-101 (ADR-0127 決定 3・FR-04): 有効にすると、RAG の形の検索が段を通り、モデルの順に並ぶ。
    // T-103 (FR-05): 段へ渡るのは ABAC で落とした後の候補だけ —— 権限外（dept=hr）の本文はプロンプトに無い。
    [Fact]
    public async Task 有効にするとRAGの形の検索に掛かり権限外は送られない()
    {
        var client = FakeRerankClient.Answering("{\"ranking\":[3,2,1]}");
        await using var factory = new RerankEnabledFactory(client);
        await SeedAsync(factory,
            Chunk("四半期 売上 アルファ", "sales"),
            Chunk("四半期 売上 ベータ", "sales"),
            Chunk("四半期 売上 ガンマ", "sales"),
            Chunk("四半期 売上 人事の機密", "hr"));

        factory.Services.GetService<ISearchReranker>().Should().BeOfType<ClaudeSearchReranker>();
        var resp = await factory.CreateClient().PostAsJsonAsync("/search", RagShaped("四半期 売上"),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>(TestContext.Current.CancellationToken);
        var prompt = client.Requests.Single().Prompt;
        prompt.Should().NotContain("人事の機密", "権限外の文書は段へ渡らない");
        client.Requests[0].Purpose.Should().Be(SearchRerankOptions.Purpose);
        client.Requests[0].Confidentiality.Should().Be(ConfidentialityLevels.Internal);
        body!.Results.Should().HaveCount(3);
        body.Results.Should().NotContain(r => r.Text.Contains("人事"));
        // 送った順（プロンプトの id 1..3）の逆になる。
        var sentOrder = new[] { "アルファ", "ベータ", "ガンマ" }
            .OrderBy(w => prompt.IndexOf(w, StringComparison.Ordinal)).ToArray();
        body.Results.Select(r => r.Text.Split(' ').Last()).Should().Equal(sentOrder.Reverse());
    }
}
