using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using AwesomeAssertions;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// FR-11, ADR-0010, ADR-0025, IADR-0531 (#1875・planning#783): Anthropic へ実際に出ていく**要求本文**を固定する。
//
// 5.5 系（opus-5-5 / sonnet-5-5 / haiku-5-5）は、thinking の無効化・budget_tokens・非既定の temperature / top_p / top_k・
// 強制の tool_choice・assistant prefill を 400 で拒む。いまの実装はどれも送っていないが、**送らないことを固定する試験が
// 無かった**（SDK の既定や MessageParameters への 1 行の追加で、全件 400 に変わり得る）。
// あわせて、用途別 effort（`Llm:PurposeEffort`）が `output_config.effort` として**対応モデルにだけ**載ることを固定する。
// ［2026-10-17 / #1749・IADR-0562］SDK を公式 `Anthropic` へ移した。effort は SDK の `OutputConfig.Effort` で送る
// （旧 SDK 向けの要求本文の整形ハンドラは撤去した）。要求本文は SDK が直列化したものを本番と同じ生成点越しに捕まえる。
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

    // T-37: effort の対応集合（ClaudeEffort.CapableModels）に無いモデルへは、用途に設定があっても送らない
    // （effort を 400 で拒むモデルへ設定 1 つで全件 400 にしない）。
    // ［2026-10-17 / #1903］従前は「切り戻しで rerank を haiku-4-5 へ戻したとき」の固定だった。計画 ADR-0138 決定 7 で
    // 旧世代は許可集合から外れ切り戻しの前提は消えたが、対応集合の外へ送らない仕組みは残るので、集合の外の例として旧 ID を使う。
    [Fact]
    public async Task CompleteAsync_ModelOutsideCapableSet_DoesNotSendEffort()
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

    // T-37: effort つきの呼び出しの直後の effort なしの呼び出しへ effort が漏れない（同じ AnthropicClient を共有する）。
    [Fact]
    public async Task CompleteAsync_EffortDoesNotLeakToNextCall()
    {
        var capture = new CapturingHandler();
        var provider = Provider(capture, effort: new() { ["rerank"] = "low" });

        await provider.CompleteAsync(new CompletionRequest("q", 512, "claude-haiku-5-5", "rerank"), TestContext.Current.CancellationToken);
        await provider.CompleteAsync(new CompletionRequest("q", 512, "claude-haiku-5-5", "rag-answer"), TestContext.Current.CancellationToken);

        capture.Bodies.Should().HaveCount(2);
        capture.Bodies[1].TryGetProperty("output_config", out _).Should().BeFalse();
    }

    // ［2026-10-17 / #1749］T-38（要求本文の整形の純関数）は、対象の整形ハンドラごと撤去した。effort の直列化は上の T-37 が見る。
    // 日本語のプロンプトを \uXXXX へ膨らませないこと（送信量）は SDK の直列化に任せる。素のまま送られることだけを固定する。
    [Fact]
    public async Task CompleteAsync_JapanesePrompt_IsSentAsIs()
    {
        var capture = new CapturingHandler();
        var provider = Provider(capture, effort: new() { ["rerank"] = "low" });

        await provider.CompleteAsync(new CompletionRequest("並べ替えて", 512, "claude-haiku-5-5", "rerank"), TestContext.Current.CancellationToken);

        var message = capture.SingleBody().GetProperty("messages")[0];
        var content = message.GetProperty("content");
        var text = content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : content[0].GetProperty("text").GetString();
        text.Should().Be("並べ替えて");
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

    // T-42, FR-11, ADR-0138 決定 7 (#1903): effort の対応集合は実配備の利用許可集合（claude-managed の Models）と一致する。
    // 許可集合に無いモデルを対応集合に残さない（旧世代を外し忘れない）。許可集合へ足したモデルが effort に対応するのに
    // 対応集合へ足し忘れると、effort が黙って送られなくなる（壊れはしないが設定が効かない）ので、それも赤にする。
    [Fact]
    public void CapableModels_EqualDeployedClaudeEndpointModels()
    {
        using var factory = new TestWebApplicationFactory();

        var routing = factory.Services.GetRequiredService<IOptions<LlmRoutingOptions>>().Value;
        var claude = routing.Endpoints.Single(e => e.Name == "claude-managed");

        ClaudeEffort.CapableModels.Should().BeEquivalentTo(claude.Models);
        ClaudeEffort.CapableModels.Should().BeEquivalentTo(["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"]);
    }

    // FR-11, IADR-0531 (#1875) T-40: 本番の DI 合成（Program.cs の `AddKeyedSingleton<ILlmProvider, ClaudeProvider>("claude")`）で、
    // ClaudeProvider が `IOptionsMonitor<ClaudePurposeEffortOptions>` を受け取り、既定の rerank=low が要求本文まで届く。
    // ClaudeProvider の effort 設定は省略可能な引数（既定 null）なので、DI が渡さなくても起動は通り、
    // **effort が黙って送られなくなるだけ**になる（上の試験はどれも手で組んだ ClaudeProvider なので気付けない）。
    // TestWebApplicationFactory は ILlmProvider をスタブへ差し替えるため使わず、AnthropicClient の一次ハンドラだけを記録用へ替える
    // （［2026-10-17 / #1749］生成点は本番と同じ AnthropicClientFactory.Create。送信先だけが違う）。
    [Fact]
    public async Task HostComposedClaudeProvider_ReceivesEffortOptions_AndSendsRerankLow()
    {
        var capture = new CapturingHandler();
        using var host = new CapturingAnthropicHost(capture);

        var provider = host.Services.GetRequiredKeyedService<ILlmProvider>("claude");

        provider.Should().BeOfType<ClaudeProvider>();
        await provider.CompleteAsync(new CompletionRequest("q", 1024, "claude-haiku-5-5", "rerank"), TestContext.Current.CancellationToken);
        capture.SingleBody().GetProperty("output_config").GetProperty("effort").GetString().Should().Be("low");
    }

    // 本番の Program.cs の登録をそのまま使い、AnthropicClient の送信先（一次ハンドラ）だけを記録用ハンドラへ差し替えるホスト。
    private sealed class CapturingAnthropicHost(CapturingHandler capture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Llm:ApiKey"] = "test-key",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                    ["Auth:Authority"] = TestServiceTokens.Issuer,
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AnthropicClient>();
                services.AddSingleton(sp => AnthropicClientFactory.Create(
                    sp.GetRequiredService<IConfiguration>(), primaryHandler: capture));
            });
        }
    }

    private static ClaudeProvider Provider(CapturingHandler capture, Dictionary<string, string>? effort)
        => TestClaudeProviders.Create(capture, effort: effort);

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
