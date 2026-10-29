using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Wolverine;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// NFR-19, ADR-0006, [[IADR-0649]] (#2129): HTTP の要求 → メッセージ → 後段のコンシューマが**同じトレース**に載る。
//
// 従前の AddPlatformObservability は Wolverine の ActivitySource を聞いていなかった。聞き手が居ないと
// Wolverine は受信側の Activity を作らず（StartActivity が null を返す）、エンベロープが運ぶ親（traceparent）は
// 使われない。コンシューマのログ・スパンはトレースから切れ、「相関 ID で突合する」（ADR-0006）がメッセージの
// 1 ホップ目で途切れていた。ここでは本番と同じ拡張でホストを組み、ローカルキュー越しに確かめる。
public class TraceContinuityTests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            // 送り先は到達しない宛先にする（試験は収集されたスパンを手元の processor で見る）。
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Otlp:Endpoint"] = "http://127.0.0.1:9" })
            .Build();

    [Fact]
    public void トレースはWolverineのActivitySourceを聞いている()
    {
        var services = new ServiceCollection();
        services.AddPlatformObservability(Config(), "trace-test");
        using var sp = services.BuildServiceProvider();
        _ = sp.GetRequiredService<TracerProvider>();

        using var wolverine = new ActivitySource(ObservabilityExtensions.WolverineActivitySourceName);
        wolverine.HasListeners().Should().BeTrue("聞き手が居ないと Wolverine は受信側のスパンを作らず、トレースが途切れる");
    }

    [Fact]
    public void 計装の名前はWolverine自身のActivitySourceの名前と一致する()
    {
        // 名前を写し間違えると、聞き手は別の名前を聞いて黙って空振りする。Wolverine の実物から引いて突き合わせる。
        var tracing = typeof(IMessageBus).Assembly.GetType("Wolverine.Runtime.WolverineTracing", throwOnError: true)!;
        var source = (ActivitySource)tracing.GetProperty("ActivitySource")!.GetValue(null)!;
        source.Name.Should().Be(ObservabilityExtensions.WolverineActivitySourceName);
    }

    [Fact]
    public async Task HTTPの要求からメッセージのコンシューマまで同じトレースに載る()
    {
        var spans = new List<Activity>();
        PingHandler.Reset();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddConfiguration(Config());
        builder.Services.AddPlatformObservability(builder.Configuration, "trace-test");
        builder.Services.ConfigureOpenTelemetryTracerProvider(t => t.AddProcessor(new Collect(spans)));
        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PingHandler));
            opts.PublishMessage<Ping>().ToLocalQueue("trace-ping");
        });

        await using var app = builder.Build();
        app.MapPost("/send", async (IMessageBus bus) =>
        {
            await bus.PublishAsync(new Ping());
            return Results.Accepted();
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var resp = await app.GetTestClient().PostAsync("/send", null, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted);

        var observed = await PingHandler.Observed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await app.StopAsync(TestContext.Current.CancellationToken);

        Activity request;
        // ASP.NET Core の ActivitySource はプロセス全体で 1 つなので、並走する他の試験の要求も拾う。この試験の要求（/send）に絞る。
        lock (spans) request = spans.Single(a => a.Source.Name == "Microsoft.AspNetCore" && a.GetTagItem("url.path") as string == "/send");
        observed.Should().Be(request.TraceId.ToString(),
            "コンシューマの処理が HTTP の要求と同じトレースに載っていない（メッセージの 1 ホップ目で相関が切れる）");
    }

    public sealed record Ping;

    public static class PingHandler
    {
        public static TaskCompletionSource<string?> Observed { get; private set; } = NewSource();

        private static TaskCompletionSource<string?> NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static void Reset() => Observed = NewSource();

        // 処理中の Activity（Wolverine が受信時に作る）のトレース ID を記録する。作られなければ null。
        public static void Handle(Ping _) => Observed.TrySetResult(Activity.Current?.TraceId.ToString());
    }

    private sealed class Collect(List<Activity> sink) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            lock (sink) sink.Add(data);
        }
    }
}
