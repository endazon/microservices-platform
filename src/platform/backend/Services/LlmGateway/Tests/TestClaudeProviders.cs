using System.Net;
using System.Text;
using LlmGateway.Common.Observability;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests;

// IADR-0562 (#1749): 試験用の ClaudeProvider を、**本番の生成点（AnthropicClientFactory.Create）を通して**組む。
// 送信先（一次ハンドラ）だけを差し替えるので、再試行 0・期限・送信先 URL などの設定は本番と同じものが効く
// （手で AnthropicClient を組むと、本番の設定を外した形を試してしまう）。
internal static class TestClaudeProviders
{
    public static ClaudeProvider Create(
        HttpMessageHandler handler,
        IDictionary<string, string?>? settings = null,
        IReadOnlyDictionary<string, string>? effort = null,
        AnthropicContentBlockMetrics? metrics = null,
        ILoggerFactory? loggers = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();
        var client = AnthropicClientFactory.Create(config, primaryHandler: handler);
        var options = effort is null
            ? null
            : new StaticOptionsMonitor<ClaudePurposeEffortOptions>(new ClaudePurposeEffortOptions
            {
                Purposes = new Dictionary<string, string>(effort, StringComparer.OrdinalIgnoreCase),
            });
        return new ClaudeProvider(
            client, config, metrics ?? TestContentBlockMetrics.Create(),
            loggers is null ? NullLogger<ClaudeProvider>.Instance : new Logger<ClaudeProvider>(loggers),
            options);
    }

    public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

// 固定応答を返し、受けた要求の回数を数えるスタブ（Anthropic への実送信を伴わない）。
internal sealed class StubAnthropicHandler(
    string body, string mediaType = "application/json", HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        });
    }
}
