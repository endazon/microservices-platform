using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Tests.Testing;
using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Introspection;

// FR-15, ADR-0018, IADR-0029 (#901): 自己申告の収集器を固定する。
//
// 🔴 **本ファイルが塞ぐ穴。** HttpEffectiveConfigCollector を参照するテストは
// リポジトリ全体に 1 件も無かった（issue #901 の先行走査が「CollectAsync で引くと 3 件出るが
// すべてテスト側の偽実装で、実装自体は 1 件も引かれていない」と記録している）。
//
// この収集器が守っているのは **「適用漏れ」と「到達不能」を混ぜないこと**である（IADR-0029）。
// 混ざるとドリフト検出が誤検知だらけになるか、逆に本物の不一致を到達不能として黙らせる。
// どちらも FR-15「不一致を検出・警告する」が静かに効かなくなる向きである。
public class HttpEffectiveConfigCollectorTests
{
    private const string SelfReportPath = "/internal/introspection";

    // service 名 → その要求への応答を決める関数。到達不能は例外で表現する。
    private sealed class RoutingHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentBag<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private sealed class CapturingClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient? Created { get; private set; }
        public string? RequestedName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            RequestedName = name;
            return Created = new HttpClient(handler, disposeHandler: false);
        }
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // 自己申告 1 件分の最小 JSON（GetFromJsonAsync の既定は camelCase）。
    private static string ReportJson(string service) => $$"""
        {"service":"{{service}}","steps":[],"ports":[],"connectors":[]}
        """;

    private static IntrospectionOptions Options(
        Dictionary<string, string> services, int timeoutSeconds = 5, string? path = null) =>
        new()
        {
            Services = services,
            TimeoutSeconds = timeoutSeconds,
            Path = path ?? SelfReportPath,
        };

    private static (HttpEffectiveConfigCollector Collector, CapturingClientFactory Factory, RecordingLogger<HttpEffectiveConfigCollector> Logger)
        Build(IntrospectionOptions options, HttpMessageHandler handler)
    {
        var factory = new CapturingClientFactory(handler);
        var logger = new RecordingLogger<HttpEffectiveConfigCollector>();
        return (new HttpEffectiveConfigCollector(factory, Microsoft.Extensions.Options.Options.Create(options), logger),
            factory, logger);
    }

    // ── 正常系（対照条件）────────────────────────────────────────────────────
    //
    // 到達不能系だけを書くと「常に unreachable」の壊れた実装でも通る。先に正常系を固定する。

    // FR-15: 応答したサービスは Services と ReachableServices の両方に載る。
    [Fact]
    public async Task 応答したサービスは実効構成と到達済み集合の双方へ載る()
    {
        var handler = new RoutingHandler(uri => Ok(ReportJson(uri.Host)));
        var (collector, _, _) = Build(
            Options(new()
            {
                ["document-service"] = "http://document-service:5001",
                ["conversion-service"] = "http://conversion-service:5002",
            }),
            handler);

        var result = await collector.CollectAsync(TestContext.Current.CancellationToken);

        result.Services.Should().HaveCount(2);
        result.ReachableServices.Should().BeEquivalentTo(["document-service", "conversion-service"]);
        result.UnreachableServices.Should().BeEmpty();
    }

    // FR-15: 収集先は「ベース URL の末尾スラッシュを落として Path を連結」した URL。
    // ここがずれると全サービスが一斉に到達不能になる（＝ドリフト検出が丸ごと沈黙する）。
    [Fact]
    public async Task 収集先URLはベースURLの末尾スラッシュを落としてパスを連結する()
    {
        var handler = new RoutingHandler(uri => Ok(ReportJson("document-service")));
        var (collector, _, _) = Build(
            Options(new() { ["document-service"] = "http://document-service:5001/" }),
            handler);

        await collector.CollectAsync(TestContext.Current.CancellationToken);

        handler.Requested.Should().ContainSingle()
            .Which.ToString().Should().Be("http://document-service:5001" + SelfReportPath);
    }

    // FR-15: 収集は名前つきクライアント（Introspection）で行う。
    // 名前がずれると、この経路に当てた mTLS / 回復性ポリシーの設定が外れる。
    [Fact]
    public async Task 収集は名前つきHTTPクライアントを使う()
    {
        var handler = new RoutingHandler(_ => Ok(ReportJson("document-service")));
        var (collector, factory, _) = Build(
            Options(new() { ["document-service"] = "http://document-service:5001" }), handler);

        await collector.CollectAsync(TestContext.Current.CancellationToken);

        factory.RequestedName.Should().Be(HttpEffectiveConfigCollector.HttpClientName);
    }

    // FR-15: 収集対象が空なら HTTP を 1 本も出さない（無設定のときに外へ出ない）。
    [Fact]
    public async Task 収集対象が空ならHTTPを一本も出さない()
    {
        var handler = new RoutingHandler(_ => Ok(ReportJson("never")));
        var (collector, _, _) = Build(Options([]), handler);

        var result = await collector.CollectAsync(TestContext.Current.CancellationToken);

        handler.Requested.Should().BeEmpty();
        result.Services.Should().BeEmpty();
        result.ReachableServices.Should().BeEmpty();
        result.UnreachableServices.Should().BeEmpty();
    }

    // ── 到達不能への変換（IADR-0029 の本題）──────────────────────────────────

    // 🔴 FR-15, IADR-0029: **1 サービスの障害が収集全体を落とさない。**
    // 例外は「そのサービスだけ UnreachableServices」へ変換し、残りは収集を続ける。
    // ここが壊れると、1 サービスが落ちただけで実効構成が丸ごと空になり、ドリフト検出が
    // 「全段が消えた」と誤警告する（あるいは例外で検出そのものが止まる）。
    [Fact]
    public async Task 一つのサービスの通信失敗は他の収集を止めず到達不能へ隔離される()
    {
        var handler = new RoutingHandler(uri => uri.Host == "broken-service"
            ? throw new HttpRequestException("connection refused")
            : Ok(ReportJson(uri.Host)));
        var (collector, _, logger) = Build(
            Options(new()
            {
                ["document-service"] = "http://document-service:5001",
                ["broken-service"] = "http://broken-service:5009",
            }),
            handler);

        var result = await collector.CollectAsync(TestContext.Current.CancellationToken);

        result.ReachableServices.Should().BeEquivalentTo(["document-service"]);
        result.UnreachableServices.Should().BeEquivalentTo(["broken-service"]);
        result.Services.Should().ContainSingle().Which.Service.Should().Be("document-service");
        logger.OfLevel(LogLevel.Warning).Should().ContainSingle()
            .Which.Exception.Should().BeOfType<HttpRequestException>(
                "到達不能は原因つきで記録しないと、運用時に切り分けられない");
    }

    // FR-15: 応答したが本文が空（JSON null）＝自己申告として使えない。到達済みへ数えない。
    // 「200 を返す壊れたサービス」を到達済みに数えると、ドリフト検出はその段を
    // 「宣言はあるが実効に無い」＝本物の不一致として警告してしまう（誤検知）。
    [Fact]
    public async Task 応答が空本文のサービスは到達不能として扱う()
    {
        var handler = new RoutingHandler(_ => Ok("null"));
        var (collector, _, logger) = Build(
            Options(new() { ["document-service"] = "http://document-service:5001" }), handler);

        var result = await collector.CollectAsync(TestContext.Current.CancellationToken);

        result.Services.Should().BeEmpty();
        result.ReachableServices.Should().BeEmpty();
        result.UnreachableServices.Should().BeEquivalentTo(["document-service"]);
        logger.OfLevel(LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("empty body");
    }

    // FR-15: 不正な JSON も到達不能へ縮退する（例外を外へ出さない）。
    [Fact]
    public async Task 不正なJSONを返すサービスは例外を外へ出さず到達不能になる()
    {
        var handler = new RoutingHandler(_ => Ok("{ this is not json"));
        var (collector, _, _) = Build(
            Options(new() { ["document-service"] = "http://document-service:5001" }), handler);

        var result = await collector.CollectAsync(TestContext.Current.CancellationToken);

        result.UnreachableServices.Should().BeEquivalentTo(["document-service"]);
    }

    // 🔴 FR-15: **停止要求（キャンセル）は「到達不能」に化けさせない。**
    // catch の when 条件 `ex is not OperationCanceledException` が守っているのはここである。
    // 握ると、シャットダウン中の収集が「全サービス到達不能」という観測結果として残り、
    // ドリフト検出の履歴に偽の障害が記録される。
    [Fact]
    public async Task キャンセルは到達不能へ化けさせず伝播する()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new RoutingHandler(_ => throw new OperationCanceledException(cts.Token));
        var (collector, _, _) = Build(
            Options(new() { ["document-service"] = "http://document-service:5001" }), handler);

        var act = async () => await collector.CollectAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── タイムアウトの下限 ────────────────────────────────────────────────────

    // FR-15: タイムアウトは 1 秒を下限に丸める。
    // 🔴 HttpClient.Timeout は 0 や負値で ArgumentOutOfRangeException を投げる ——
    // 設定ミス（未設定＝0）で**収集器が起動時に例外を吐く**ことになる。Math.Max(1, …) はその防壁。
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    public async Task 収集タイムアウトは一秒を下限に丸められる(int configured, int expectedSeconds)
    {
        var handler = new RoutingHandler(_ => Ok(ReportJson("document-service")));
        var (collector, factory, _) = Build(
            Options(new() { ["document-service"] = "http://document-service:5001" }, timeoutSeconds: configured),
            handler);

        await collector.CollectAsync(TestContext.Current.CancellationToken);

        factory.Created!.Timeout.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    // FR-15: Path は設定で差し替えられる（自己申告パスの単一情報源は IntrospectionOptions）。
    [Fact]
    public async Task 自己申告パスは設定で差し替えられる()
    {
        var handler = new RoutingHandler(_ => Ok(ReportJson("document-service")));
        var (collector, _, _) = Build(
            Options(new() { ["document-service"] = "http://document-service:5001" }, path: "/internal/custom"),
            handler);

        await collector.CollectAsync(TestContext.Current.CancellationToken);

        handler.Requested.Should().ContainSingle()
            .Which.AbsolutePath.Should().Be("/internal/custom");
    }

    // FR-15: 自己申告の中身（段・ポート・コネクタ）はそのまま運ばれる。
    // 収集器が中身を落とすと、ドリフト検出は「宣言はあるが実効に無い」と誤警告する。
    [Fact]
    public async Task 自己申告の段とポートとコネクタはそのまま運ばれる()
    {
        var handler = new RoutingHandler(_ => Ok("""
            {"service":"conversion-service",
             "steps":[{"name":"convert","consumer":"C","input":"RawDocumentFetched","outputs":["DocumentNormalized"],"enabled":true}],
             "ports":[{"port":"object-storage","implementation":"S3ObjectStorageClient","target":"minio:9000"}],
             "connectors":[{"name":"obsidian","enabled":false}]}
            """));
        var (collector, _, _) = Build(
            Options(new() { ["conversion-service"] = "http://conversion-service:5002" }), handler);

        var result = await collector.CollectAsync(TestContext.Current.CancellationToken);

        var report = result.Services.Should().ContainSingle().Which;
        report.Service.Should().Be("conversion-service");
        report.Steps.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new StepIntrospectionDto("convert", "C", "RawDocumentFetched", ["DocumentNormalized"], true));
        report.Ports.Should().ContainSingle().Which.Target.Should().Be("minio:9000");
        report.Connectors.Should().ContainSingle().Which.Enabled.Should().BeFalse();
    }
}
