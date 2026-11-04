using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using LlmGateway.Domain.Ports;
using LlmGateway.Infrastructure.ExternalServices;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// FR-10, FR-11, ADR-0022, ADR-0044 決定 3, IADR-0671 (#2396): プロンプトキャッシュの要求の形と、応答のキャッシュの書き・読みの読み取り。
//
// 提供元の仕様（2026-11-05 に一次情報で確認）: cache_control は messages[].content の text ブロックに置ける。既定の TTL は 5 分
// （{"type":"ephemeral"}）。応答の input_tokens は**最後の区切りの後ろの残りだけ**で、プロンプト全体は
// input_tokens + cache_creation_input_tokens + cache_read_input_tokens である。
//
// 🔴 接頭辞の無い要求は従前と同じ形で送る（content は文字列・cache_control なし）。接頭辞があっても model・max_tokens・
// output_config（effort）・messages の件数は変えない（判断の内容を変えない）。
[Trait("TestKind", "Unit")]
public class ClaudeProviderPromptCacheTests
{
    private const string Prefix = "# 固定の前文\n当日の方針と監視銘柄。\n";
    private const string Rest = "# 銘柄固有部\nAAPL の判断を返す。";

    // 応答の usage（残り 1,000・書き 2,000・読み 10,000・出力 500）。プロンプト全体は 13,000。
    private const string UsageJson =
        "\"usage\":{\"input_tokens\":1000,\"cache_creation_input_tokens\":2000,\"cache_read_input_tokens\":10000,\"output_tokens\":500}";

    // T-2396-01: 接頭辞を分けた一括の要求は、user 1 件の content が [接頭辞（cache_control: ephemeral・ttl なし）, 残り] になる。
    [Fact]
    public async Task CompleteAsync_WithCacheablePrefix_SendsPrefixBlockWithCacheControl()
    {
        var capture = new CapturingHandler();
        var provider = TestClaudeProviders.Create(capture, effort: new Dictionary<string, string> { ["trade-decision"] = "high" });

        await provider.CompleteAsync(
            new CompletionRequest(Rest, 2048, "claude-sonnet-5-5", "trade-decision", CacheablePrefix: Prefix),
            TestContext.Current.CancellationToken);

        AssertCachedShape(capture.SingleBody());
    }

    // T-2396-01: 逐次の要求も同じ形（要求の組み立ては一括と共通）。
    [Fact]
    public async Task StreamAsync_WithCacheablePrefix_SendsPrefixBlockWithCacheControl()
    {
        var capture = new CapturingHandler();
        var provider = TestClaudeProviders.Create(capture, effort: new Dictionary<string, string> { ["trade-decision"] = "high" });

        await foreach (var _ in provider.StreamAsync(
            new CompletionRequest(Rest, 2048, "claude-sonnet-5-5", "trade-decision", CacheablePrefix: Prefix),
            TestContext.Current.CancellationToken))
        {
        }

        AssertCachedShape(capture.SingleBody());
    }

    // T-2396-02: 接頭辞の無い要求は従前と同じ形（content は文字列。cache_control は要求のどこにも無い）。空文字も「無い」。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CompleteAsync_WithoutCacheablePrefix_KeepsLegacyShape(string? prefix)
    {
        var capture = new CapturingHandler();
        var provider = TestClaudeProviders.Create(capture);

        await provider.CompleteAsync(
            new CompletionRequest(Prefix + Rest, 2048, "claude-sonnet-5-5", "trade-decision", CacheablePrefix: prefix),
            TestContext.Current.CancellationToken);

        var body = capture.SingleBody();
        var content = body.GetProperty("messages").EnumerateArray().Single().GetProperty("content");
        content.ValueKind.Should().Be(JsonValueKind.String);
        content.GetString().Should().Be(Prefix + Rest);
        capture.RawBodies.Single().Should().NotContain("cache_control");
    }

    // T-2396-03: 接頭辞の有無で model・max_tokens・output_config・messages の件数と役割は変わらない。
    // モデルが読む本文は接頭辞＋残りの連結で同じ（ブロックの区切りに文字を足さない）。
    [Fact]
    public async Task CompleteAsync_CacheablePrefix_DoesNotChangeDecisionParameters()
    {
        var capture = new CapturingHandler();
        var provider = TestClaudeProviders.Create(capture, effort: new Dictionary<string, string> { ["trade-decision"] = "high" });
        var ct = TestContext.Current.CancellationToken;

        await provider.CompleteAsync(new CompletionRequest(Prefix + Rest, 2048, "claude-haiku-5-5", "trade-decision"), ct);
        await provider.CompleteAsync(new CompletionRequest(Rest, 2048, "claude-haiku-5-5", "trade-decision", CacheablePrefix: Prefix), ct);

        var (legacy, cached) = (capture.Bodies[0], capture.Bodies[1]);
        cached.GetProperty("model").GetString().Should().Be(legacy.GetProperty("model").GetString());
        cached.GetProperty("max_tokens").GetInt32().Should().Be(legacy.GetProperty("max_tokens").GetInt32());
        cached.GetProperty("output_config").GetRawText().Should().Be(legacy.GetProperty("output_config").GetRawText());
        PropertyNames(cached).Should().BeEquivalentTo(PropertyNames(legacy), "要求のトップレベルの鍵（system・thinking 等）を足さない");

        var legacyMessage = legacy.GetProperty("messages").EnumerateArray().Single();
        var cachedMessage = cached.GetProperty("messages").EnumerateArray().Single();
        cachedMessage.GetProperty("role").GetString().Should().Be(legacyMessage.GetProperty("role").GetString());
        var joined = string.Concat(cachedMessage.GetProperty("content").EnumerateArray().Select(b => b.GetProperty("text").GetString()));
        joined.Should().Be(legacyMessage.GetProperty("content").GetString());
    }

    // T-2396-04: 残りが空なら接頭辞のブロック 1 個だけを送る（空の text ブロックは提供元が 400 で拒む）。
    [Fact]
    public async Task CompleteAsync_CacheablePrefixWithEmptyRest_SendsSingleCachedBlock()
    {
        var capture = new CapturingHandler();
        var provider = TestClaudeProviders.Create(capture);

        await provider.CompleteAsync(
            new CompletionRequest(string.Empty, 512, "claude-sonnet-5-5", "trade-decision", CacheablePrefix: Prefix),
            TestContext.Current.CancellationToken);

        var blocks = capture.SingleBody().GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToList();
        blocks.Should().ContainSingle();
        blocks[0].GetProperty("text").GetString().Should().Be(Prefix);
        blocks[0].GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");
    }

    // T-2396-05: 一括の応答の書き・読みを結果に載せ、InputTokens はプロンプト全体（残り＋書き＋読み）にする。
    [Fact]
    public async Task CompleteAsync_ReadsCacheUsage_AndReportsWholePromptAsInputTokens()
    {
        var provider = TestClaudeProviders.Create(new CapturingHandler());

        var result = await provider.CompleteAsync(
            new CompletionRequest(Rest, 512, "claude-sonnet-5-5", "trade-decision", CacheablePrefix: Prefix),
            TestContext.Current.CancellationToken);

        result.InputTokens.Should().Be(13_000);
        result.CacheCreationInputTokens.Should().Be(2_000);
        result.CacheReadInputTokens.Should().Be(10_000);
        result.OutputTokens.Should().Be(500);
    }

    // T-2396-05: 逐次は message_start の書き・読みを、経過報告のチャンクと最終チャンクの両方に載せる（IADR-0601 の判明分も同じ値）。
    [Fact]
    public async Task StreamAsync_ReadsCacheUsageFromMessageStart()
    {
        var provider = TestClaudeProviders.Create(new CapturingHandler());

        var chunks = new List<CompletionChunk>();
        await foreach (var chunk in provider.StreamAsync(
            new CompletionRequest(Rest, 512, "claude-sonnet-5-5", "rag-answer", CacheablePrefix: Prefix),
            TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        var progress = chunks.First(c => !c.Done && c.InputTokens > 0);
        progress.InputTokens.Should().Be(13_000);
        progress.CacheCreationInputTokens.Should().Be(2_000);
        progress.CacheReadInputTokens.Should().Be(10_000);

        var done = chunks.Single(c => c.Done);
        done.InputTokens.Should().Be(13_000);
        done.CacheCreationInputTokens.Should().Be(2_000);
        done.CacheReadInputTokens.Should().Be(10_000);
        done.OutputTokens.Should().Be(500);
    }

    // T-2396-05: キャッシュの欄が無い応答（従前の応答）は書き・読みとも 0 で、InputTokens は input_tokens のまま。
    [Fact]
    public async Task CompleteAsync_ResponseWithoutCacheFields_KeepsLegacyCounts()
    {
        var provider = TestClaudeProviders.Create(new CapturingHandler(usageJson: "\"usage\":{\"input_tokens\":7,\"output_tokens\":3}"));

        var result = await provider.CompleteAsync(
            new CompletionRequest("q", 512, "claude-sonnet-5-5", "rag-answer"), TestContext.Current.CancellationToken);

        result.InputTokens.Should().Be(7);
        result.CacheCreationInputTokens.Should().Be(0);
        result.CacheReadInputTokens.Should().Be(0);
    }

    private static void AssertCachedShape(JsonElement body)
    {
        body.TryGetProperty("system", out _).Should().BeFalse("接頭辞は system へ移さない（指示の役割を変えない）");
        body.TryGetProperty("cache_control", out _).Should().BeFalse("自動キャッシュ（トップレベル）は使わない");
        body.GetProperty("model").GetString().Should().Be("claude-sonnet-5-5");
        body.GetProperty("max_tokens").GetInt32().Should().Be(2048);
        body.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("high");

        var message = body.GetProperty("messages").EnumerateArray().Should().ContainSingle().Subject;
        message.GetProperty("role").GetString().Should().Be("user");
        var blocks = message.GetProperty("content").EnumerateArray().ToList();
        blocks.Should().HaveCount(2);

        blocks[0].GetProperty("type").GetString().Should().Be("text");
        blocks[0].GetProperty("text").GetString().Should().Be(Prefix);
        var cacheControl = blocks[0].GetProperty("cache_control");
        cacheControl.GetProperty("type").GetString().Should().Be("ephemeral");
        // 1 時間の TTL（書きが 2 倍）は使わない。送らない（既定 5 分）か、送るなら 5m。
        if (cacheControl.TryGetProperty("ttl", out var ttl) && ttl.ValueKind != JsonValueKind.Null)
            ttl.GetString().Should().Be("5m");

        blocks[1].GetProperty("type").GetString().Should().Be("text");
        blocks[1].GetProperty("text").GetString().Should().Be(Rest);
        // 区切りは接頭辞の末尾だけ（残りには置かない）。
        (blocks[1].TryGetProperty("cache_control", out var restCache) && restCache.ValueKind != JsonValueKind.Null)
            .Should().BeFalse();
    }

    private static IEnumerable<string> PropertyNames(JsonElement e) => e.EnumerateObject().Select(p => p.Name);

    // 送られた要求本文を記録し、キャッシュの書き・読みを含む最小の正常応答を返す（stream の有無に合わせる）。
    private sealed class CapturingHandler(string usageJson = UsageJson) : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        public List<string> RawBodies { get; } = [];

        public JsonElement SingleBody() => Bodies.Should().ContainSingle().Subject;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var text = await request.Content!.ReadAsStringAsync(ct);
            RawBodies.Add(text);
            var body = JsonDocument.Parse(text).RootElement.Clone();
            Bodies.Add(body);

            var stream = body.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
            if (!stream)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"x\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],"
                        + "\"stop_reason\":\"end_turn\"," + usageJson + "}",
                        Encoding.UTF8, "application/json"),
                };

            // message_start の usage は出力 0（提供元の逐次と同じ）。出力は message_delta で確定する。
            var startUsage = usageJson.Replace("\"output_tokens\":500", "\"output_tokens\":0", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: message_start\n"
                    + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"x\",\"content\":[],\"stop_reason\":null," + startUsage + "}}\n\n"
                    + "event: message_delta\n"
                    + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":500}}\n\n"
                    + "event: message_stop\n"
                    + "data: {\"type\":\"message_stop\"}\n\n",
                    Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
