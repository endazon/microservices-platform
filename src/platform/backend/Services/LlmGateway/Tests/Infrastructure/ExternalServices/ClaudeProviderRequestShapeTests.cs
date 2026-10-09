using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic.SDK;
using AwesomeAssertions;
using LlmGateway.Domain.Ports;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// FR-11, ADR-0010, ADR-0025, IADR-0531 (#1875・planning#783): Anthropic へ実際に出ていく**要求本文**を固定する。
//
// 5.5 系（opus-5-5 / sonnet-5-5 / haiku-5-5）は、thinking の無効化・budget_tokens・非既定の temperature / top_p / top_k・
// 強制の tool_choice・assistant prefill を 400 で拒む。いまの実装はどれも送っていないが、**送らないことを固定する試験が
// 無かった**（SDK の既定や MessageParameters への 1 行の追加で、全件 400 に変わり得る）。
// あわせて、用途別 effort（`Llm:PurposeEffort`）が `output_config.effort` として**対応モデルにだけ**載ることを固定する。
[Trait("TestKind", "Unit")]
public class ClaudeProviderRequestShapeTests
{
    // 5.5 系で 400 になる要求キー（トップレベル）。
    private static readonly string[] ForbiddenKeys = ["temperature", "top_p", "top_k", "thinking", "tool_choice"];

    public static TheoryData<string> Models55() => ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"];

    // T-36: 非ストリームの要求本文に 400 になるキーが無く、effort 未設定なら output_config も無い。
    [Theory]
    [MemberData(nameof(Models55))]
    public async Task CompleteAsync_RequestBody_HasNoKeysRejectedBy55Models(string model)
    {
        var capture = new CapturingHandler();
        var provider = Provider(capture, effort: null);

        await provider.CompleteAsync(new CompletionRequest("q", 512, model, "analysis"), TestContext.Current.CancellationToken);

        var body = capture.SingleBody();
        body.GetProperty("model").GetString().Should().Be(model);
        foreach (var key in ForbiddenKeys)
            body.TryGetProperty(key, out _).Should().BeFalse($"{key} は 5.5 系で 400 になる");
        body.TryGetProperty("output_config", out _).Should().BeFalse("effort を設定していない用途は提供元の既定に任せる");
        // assistant prefill を送らない（messages は user 1 件だけ）。
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        messages.Should().ContainSingle();
        messages[0].GetProperty("role").GetString().Should().Be("user");
    }

    // T-36: ストリームの要求本文も同じ（stream=true 以外は非ストリームと同じ形）。
    [Theory]
    [MemberData(nameof(Models55))]
    public async Task StreamAsync_RequestBody_HasNoKeysRejectedBy55Models(string model)
    {
        var capture = new CapturingHandler();
        var provider = Provider(capture, effort: null);

        await foreach (var _ in provider.StreamAsync(new CompletionRequest("q", 512, model, "rag-answer"), TestContext.Current.CancellationToken))
        {
        }

        var body = capture.SingleBody();
        body.GetProperty("stream").GetBoolean().Should().BeTrue();
        foreach (var key in ForbiddenKeys)
            body.TryGetProperty(key, out _).Should().BeFalse($"{key} は 5.5 系で 400 になる");
        body.TryGetProperty("output_config", out _).Should().BeFalse();
    }

    // T-37: 用途 rerank に effort low を設定すると、haiku-5-5 への要求本文に output_config.effort=low が載る（非ストリーム）。
    [Fact]
    public async Task CompleteAsync_RerankOnHaiku55_SendsConfiguredEffort()
    {
        var capture = new CapturingHandler();
        var provider = Provider(capture, effort: new() { ["rerank"] = "low" });

        await provider.CompleteAsync(new CompletionRequest("q", 1024, "claude-haiku-5-5", "rerank"), TestContext.Current.CancellationToken);

        var body = capture.SingleBody();
        body.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("low");
        foreach (var key in ForbiddenKeys)
            body.TryGetProperty(key, out _).Should().BeFalse();
    }

    // T-37: ストリーム経路にも同じ effort が載る（要求の送信は反復子の最初の段で起きる）。
    [Fact]
    public async Task StreamAsync_ConfiguredPurpose_SendsEffort()
    {
        var capture = new CapturingHandler();
        var provider = Provider(capture, effort: new() { ["rag-answer"] = "medium" });

        await foreach (var _ in provider.StreamAsync(new CompletionRequest("q", 512, "claude-sonnet-5-5", "rag-answer"), TestContext.Current.CancellationToken))
        {
        }

        capture.SingleBody().GetProperty("output_config").GetProperty("effort").GetString().Should().Be("medium");
    }

    // T-37: effort を受けないモデル（haiku-4-5 は effort を 400 で拒む）へは、用途に設定があっても送らない。
    // 切り戻しで rerank を haiku-4-5 へ戻しても、設定 1 つで全件 400 にならないことの固定。
    [Fact]
    public async Task CompleteAsync_RerankOnHaiku45_DoesNotSendEffort()
    {
        var capture = new CapturingHandler();
        var provider = Provider(capture, effort: new() { ["rerank"] = "low" });

        await provider.CompleteAsync(new CompletionRequest("q", 512, "claude-haiku-4-5", "rerank"), TestContext.Current.CancellationToken);

        capture.SingleBody().TryGetProperty("output_config", out _).Should().BeFalse();
    }

    // T-37: 設定に無い用途へは送らない（effort の設定は用途ごとで、既定値を持たない）。
    [Fact]
    public async Task CompleteAsync_UnconfiguredPurpose_DoesNotSendEffort()
    {
        var capture = new CapturingHandler();
        var provider = Provider(capture, effort: new() { ["rerank"] = "low" });

        await provider.CompleteAsync(new CompletionRequest("q", 512, "claude-haiku-5-5", "rag-answer"), TestContext.Current.CancellationToken);

        capture.SingleBody().TryGetProperty("output_config", out _).Should().BeFalse();
    }

    // T-37: 呼び出しの後に文脈が残らない（シングルトンの HttpClient を共有する次の呼び出しへ effort が漏れない）。
    [Fact]
    public async Task CompleteAsync_EffortDoesNotLeakToNextCall()
    {
        var capture = new CapturingHandler();
        var provider = Provider(capture, effort: new() { ["rerank"] = "low" });

        await provider.CompleteAsync(new CompletionRequest("q", 512, "claude-haiku-5-5", "rerank"), TestContext.Current.CancellationToken);
        await provider.CompleteAsync(new CompletionRequest("q", 512, "claude-haiku-5-5", "rag-answer"), TestContext.Current.CancellationToken);

        AnthropicRequestContext.Effort.Should().BeNull();
        capture.Bodies.Should().HaveCount(2);
        capture.Bodies[1].TryGetProperty("output_config", out _).Should().BeFalse();
    }

    // T-38: 整形の純関数。既存の output_config へ足し、呼び出し側の明示 effort は上書きしない。JSON でなければ触らない。
    [Fact]
    public void TryAddEffort_MergesWithoutOverriding()
    {
        AnthropicRequestShapingHandler.TryAddEffort("""{"model":"m","output_config":{"format":{"type":"json"}}}""", "low", out var merged)
            .Should().BeTrue();
        using (var doc = JsonDocument.Parse(merged))
        {
            var oc = doc.RootElement.GetProperty("output_config");
            oc.GetProperty("effort").GetString().Should().Be("low");
            oc.GetProperty("format").GetProperty("type").GetString().Should().Be("json");
        }

        AnthropicRequestShapingHandler.TryAddEffort("""{"output_config":{"effort":"high"}}""", "low", out var kept).Should().BeFalse();
        kept.Should().Be("""{"output_config":{"effort":"high"}}""");
        AnthropicRequestShapingHandler.TryAddEffort("not json", "low", out _).Should().BeFalse();
        AnthropicRequestShapingHandler.TryAddEffort("[1]", "low", out _).Should().BeFalse();
    }

    // T-38: 日本語のプロンプトを \uXXXX へ膨らませない（JSON として同値・送信量だけの問題）。
    [Fact]
    public void TryAddEffort_KeepsNonAsciiPromptReadable()
    {
        AnthropicRequestShapingHandler.TryAddEffort("""{"messages":[{"role":"user","content":"並べ替えて"}]}""", "low", out var shaped)
            .Should().BeTrue();
        shaped.Should().Contain("並べ替えて");
    }

    // T-39: 値域外の effort は起動時に落とす（実行時に送ると全件 400）。値域内は通す。
    [Theory]
    [InlineData("low", true)]
    [InlineData("medium", true)]
    [InlineData("high", true)]
    [InlineData("xhigh", true)]
    [InlineData("max", true)]
    [InlineData("LOW", true)]
    [InlineData("minimal", false)]
    [InlineData("", false)]
    public void Validator_AcceptsOnlyKnownLevels(string level, bool ok)
    {
        var options = new ClaudePurposeEffortOptions { Purposes = { ["rerank"] = level } };

        new ClaudePurposeEffortOptionsValidator().Validate(null, options).Succeeded.Should().Be(ok);
    }

    // T-39: 実配備の appsettings.json は rerank=low だけを持つ（他の用途は提供元の既定）。ホストの束縛を通して読む。
    [Fact]
    public void DeployedConfig_SetsRerankLowOnly()
    {
        using var factory = new TestWebApplicationFactory();

        var options = factory.Services.GetRequiredService<IOptionsMonitor<ClaudePurposeEffortOptions>>().CurrentValue;

        options.Purposes.Should().BeEquivalentTo(new Dictionary<string, string> { ["rerank"] = "low" });
        ClaudeEffort.Resolve(options, "rerank", "claude-haiku-5-5").Should().Be("low");
    }

    private static ClaudeProvider Provider(CapturingHandler capture, Dictionary<string, string>? effort)
    {
        var handler = new AnthropicRequestShapingHandler { InnerHandler = capture };
        var client = new AnthropicClient(new APIAuthentication("test-key"), new HttpClient(handler));
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var options = effort is null
            ? null
            : new StaticOptionsMonitor(new ClaudePurposeEffortOptions
            {
                Purposes = new Dictionary<string, string>(effort, StringComparer.OrdinalIgnoreCase),
            });
        return new ClaudeProvider(client, config, options);
    }

    private sealed class StaticOptionsMonitor(ClaudePurposeEffortOptions value) : IOptionsMonitor<ClaudePurposeEffortOptions>
    {
        public ClaudePurposeEffortOptions CurrentValue => value;

        public ClaudePurposeEffortOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ClaudePurposeEffortOptions, string?> listener) => null;
    }

    // 送られた要求本文を記録し、stream の有無に合わせた最小の正常応答を返す。
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        public JsonElement SingleBody() => Bodies.Should().ContainSingle().Subject;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var text = await request.Content!.ReadAsStringAsync(ct);
            var body = JsonDocument.Parse(text).RootElement.Clone();
            Bodies.Add(body);

            var stream = body.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
            return stream
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "event: message_start\n"
                        + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"x\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\n"
                        + "event: message_delta\n"
                        + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":1}}\n\n"
                        + "event: message_stop\n"
                        + "data: {\"type\":\"message_stop\"}\n\n",
                        Encoding.UTF8, "text/event-stream"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"x\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],"
                        + "\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}",
                        Encoding.UTF8, "application/json"),
                };
        }
    }
}
