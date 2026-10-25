using AwesomeAssertions;
using LlmGateway.Domain.Routing;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// T-46, FR-11, ADR-0010, IADR-0641 (#2136): Claude のエンドポイントが有効なのに API キーが空なら、起動時に止まる。
// 従前は空のキーがそのまま SDK へ渡り、最初の Claude 呼び出しの 401 で初めて分かった（readiness は通ったまま）。
// 鍵なしで動かす検証スタック（compose・values-local・dotnet run）は `Llm:AllowMissingApiKey=true` を明示する。
// 🔴 鍵の値はログにも例外にも出さない。
[Trait("TestKind", "Unit")]
public class ClaudeApiKeyStartupValidationTests
{
    // 鍵を置いたことが分かる探り値。失敗の文言・warn のどこにも現れてはならない。
    private const string ProbeKey = "sk-ant-probe-2136-must-not-leak";

    // 本物の Program.cs の配線（appsettings.json の claude-managed ＝ Enabled）で起動する器。
    // TestWebApplicationFactory は鍵に test-key を与えるので使わない。
    private sealed class Host(IDictionary<string, string?> settings, RecordingLoggerFactory? logs = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                    ["Auth:Authority"] = TestServiceTokens.Issuer,
                };
                foreach (var (k, v) in settings) values[k] = v;
                cfg.AddInMemoryCollection(values);
            });
            if (logs is not null)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ILoggerFactory>();
                    services.AddSingleton<ILoggerFactory>(logs);
                });
            }
        }
    }

    private static Exception StartupFailure(IDictionary<string, string?> settings)
    {
        using var host = new Host(settings);
        var act = () => host.Services;
        return act.Should().Throw<Exception>().Which;
    }

    private static string AllMessages(Exception e)
    {
        var parts = new List<string>();
        for (var x = e; x is not null; x = x.InnerException) parts.Add(x.Message);
        if (e is AggregateException agg) parts.AddRange(agg.InnerExceptions.Select(i => i.Message));
        return string.Join("\n", parts);
    }

    // T-46 ①: 鍵が空・空白・未設定なら起動が失敗し、文言は設定キーの名前を含む（修正前は起動してしまい赤）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 鍵が空なら起動時に失敗する(string? apiKey)
    {
        var settings = new Dictionary<string, string?>();
        if (apiKey is not null) settings[AnthropicClientFactory.ApiKeyConfigKey] = apiKey;
        else settings[AnthropicClientFactory.ApiKeyConfigKey] = null; // appsettings.json の "" を null で上書き（未設定）

        var e = StartupFailure(settings);

        var text = AllMessages(e);
        text.Should().Contain(AnthropicClientFactory.ApiKeyConfigKey);
        text.Should().Contain(ClaudeApiKeyStartupValidator.AllowMissingConfigKey);
        text.Should().Contain("claude-managed");
    }

    // T-46 ②: 明示の opt-out（true）なら鍵が空でも起動し、warn を 1 行出す（鍵の値・キー名以外は含めない）。
    [Fact]
    public void OptOutがtrueなら鍵が空でも起動しwarnを1行出す()
    {
        var logs = new RecordingLoggerFactory();
        using var host = new Host(new Dictionary<string, string?>
        {
            [AnthropicClientFactory.ApiKeyConfigKey] = "",
            [ClaudeApiKeyStartupValidator.AllowMissingConfigKey] = "true",
        }, logs);

        _ = host.Services; // 起動する（例外を投げない）

        var warns = logs.Entries
            .Where(x => x.Level == LogLevel.Warning && x.Message.Contains(AnthropicClientFactory.ApiKeyConfigKey))
            .ToList();
        warns.Should().HaveCount(1);
        warns[0].Message.Should().Contain(ClaudeApiKeyStartupValidator.AllowMissingConfigKey);
    }

    // T-46 ③: true 以外の値は opt-out にしない（fail-closed）。
    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("false")]
    [InlineData("")]
    public void OptOutがtrue以外なら起動時に失敗する(string flag)
    {
        var e = StartupFailure(new Dictionary<string, string?>
        {
            [AnthropicClientFactory.ApiKeyConfigKey] = "",
            [ClaudeApiKeyStartupValidator.AllowMissingConfigKey] = flag,
        });

        AllMessages(e).Should().Contain(AnthropicClientFactory.ApiKeyConfigKey);
    }

    // T-46 ④: 鍵があれば起動し、鍵の値はどのログにも出ない。
    [Fact]
    public void 鍵があれば起動し鍵の値はログに出ない()
    {
        var logs = new RecordingLoggerFactory();
        using var host = new Host(new Dictionary<string, string?>
        {
            [AnthropicClientFactory.ApiKeyConfigKey] = ProbeKey,
        }, logs);

        _ = host.Services;

        logs.Entries.Should().NotContain(x => x.Message.Contains(ProbeKey));
        logs.Entries.Should().NotContain(x => x.Message.Contains(AnthropicClientFactory.ApiKeyConfigKey)
            && x.Level >= LogLevel.Warning);
    }

    // T-46 ⑤: 判定器の単体。Claude のエンドポイントがすべて無効なら、鍵が空でも通る。
    [Fact]
    public void Claudeのエンドポイントが無効なら鍵が空でも通る()
    {
        var result = Validate(apiKey: "", optOut: null,
            new LlmEndpointOptions { Name = "claude-managed", Provider = "claude", Enabled = false },
            new LlmEndpointOptions { Name = "selfhosted-oss", Provider = "selfhosted", Enabled = true });

        result.Succeeded.Should().BeTrue();
    }

    // T-46 ⑤: 有効な Claude のエンドポイントが複数あれば、すべての名前を文言に含む（どれを無効にすればよいかが分かる）。
    [Fact]
    public void 有効なClaudeのエンドポイントの名前をすべて挙げる()
    {
        var result = Validate(apiKey: null, optOut: null,
            new LlmEndpointOptions { Name = "claude-a", Provider = "claude", Enabled = true },
            new LlmEndpointOptions { Name = "claude-b", Provider = "claude", Enabled = true },
            new LlmEndpointOptions { Name = "claude-off", Provider = "claude", Enabled = false });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("claude-a").And.Contain("claude-b").And.NotContain("claude-off");
    }

    // T-46 ⑥: 空白だけの鍵でも、失敗の文言に鍵の字面を差し込まない（値を引用する文言にしない）。
    [Fact]
    public void 失敗の文言は鍵の字面を含まない()
    {
        const string whitespaceKey = " \t ";
        var result = Validate(apiKey: whitespaceKey, optOut: null,
            new LlmEndpointOptions { Name = "claude-managed", Provider = "claude", Enabled = true });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().NotContain(whitespaceKey);
        result.FailureMessage.Should().NotContain("'" + whitespaceKey + "'");
    }

    private static ValidateOptionsResult Validate(string? apiKey, string? optOut, params LlmEndpointOptions[] endpoints)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AnthropicClientFactory.ApiKeyConfigKey] = apiKey,
                [ClaudeApiKeyStartupValidator.AllowMissingConfigKey] = optOut,
            })
            .Build();
        var options = new LlmRoutingOptions { Endpoints = [.. endpoints] };
        return new ClaudeApiKeyStartupValidator(configuration).Validate(Options.DefaultName, options);
    }
}
