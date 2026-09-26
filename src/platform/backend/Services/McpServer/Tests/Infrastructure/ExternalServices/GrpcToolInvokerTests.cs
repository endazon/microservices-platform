using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.Mcp.V1;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// FR-16, UC-08, NFR-09, NFR-16, ADR-0024 §2, ADR-0029, ADR-0075, ADR-0117 決定 1・2・4,
// IADR-0462（2026-09-27 追記 / #1516, #1255 経路 ④-b）: ツールの実行を gRPC で**申告したサービス**へ送る実行器。
//
// 申告元の代わりに**ループバック（127.0.0.1）の実 Kestrel**（`McpToolDeclarationGrpcTestHost`）を立て、
// 宛先の決め方（申告したサービス＋申告名。申告の URL は使わない）・s2s・fail-closed（実行口が無い・経路が無い・期限切れ・
// 拒否・到達不能）・取り消し・期限・登録を測る。**申告の URL の側にも待受を立て、接続が 1 本も来ないこと**を数える。
[Trait("TestKind", "Integration")]
public sealed class GrpcToolInvokerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Config(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static (GrpcToolInvoker Invoker, RecordingLogger<GrpcToolInvoker> Log) Build(
        IDictionary<string, string?> values, IServiceTokenProvider? tokenProvider = null)
    {
        var log = new RecordingLogger<GrpcToolInvoker>();
        return (new GrpcToolInvoker(
            Config(values), log,
            tokenProvider ?? new FixedTokenProvider(McpToolDeclarationGrpcTestHost.ServiceToken())), log);
    }

    private static PublishedTool Tool(string service, string publishedName = "search") =>
        new(publishedName, service, McpToolDeclarationGrpcTestHost.SampleTool);

    private static ToolInvocationScope Scope(bool excludePrivateNote = true) => new(
        "alice", "ServiceAccount",
        new Dictionary<string, string> { ["clearance"] = "internal" },
        excludePrivateNote, McpToolDeclarationGrpcTestHost.SampleTool.RequiredScope);

    private static string DeadAddress()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    // 何も返さない実行面（呼び出し側の取り消し・期限でだけ終わる）。
    private static McpToolDeclarationGrpcTestHost.StubExecution Hanging() => new()
    {
        Respond = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new Pb.McpToolResult();
        },
    };

    // 🔴 E-1: 陽性対照。実行は**公開構成で突き合わせたサービス**の h2c アドレスへ、s2s トークンで送られる。
    // 要求の `tool` は**申告名**（公開名ではない）。別のサービスには送られない。応答は共通エンベロープへ戻る。
    [Fact]
    public async Task Executes_on_the_declaring_service_over_grpc_with_the_declared_name()
    {
        var answered = new Pb.McpToolResult { TotalCount = 2, Truncated = true };
        answered.Documents.Add(new Pb.McpToolDocument { DocumentId = "d1", Title = "本文あり", Body = "本文", ReferenceUrl = "https://wiki/d1" });
        answered.Documents.Add(new Pb.McpToolDocument { DocumentId = "d2", Title = "本文なし" });
        answered.Documents[0].Attributes.Add("doc_scope", "organization");
        var declaring = new McpToolDeclarationGrpcTestHost.StubExecution { Respond = (_, _) => Task.FromResult(answered) };
        var other = new McpToolDeclarationGrpcTestHost.StubExecution
        {
            Respond = (_, _) => Task.FromResult(new Pb.McpToolResult { TotalCount = 99 }),
        };
        await using var a = await McpToolDeclarationGrpcTestHost.StartAsync(ct: Ct, execution: declaring);
        await using var b = await McpToolDeclarationGrpcTestHost.StartAsync(ct: Ct, execution: other);
        var (invoker, log) = Build(new Dictionary<string, string?>
        {
            ["Mcp:GrpcServices:svc-a"] = a.GrpcAddress,
            ["Mcp:GrpcServices:svc-b"] = b.GrpcAddress,
        });

        var result = await invoker.InvokeAsync(Tool("svc-a", publishedName: "search"), Scope(), """{"query":"x"}""", Ct);

        declaring.Received.Should().ContainSingle().Which.Tool.Should().Be(
            McpToolDeclarationGrpcTestHost.SampleTool.Name, "受け口は自分の申告の名前で引く（公開名 search ではない）");
        other.Received.Should().BeEmpty("申告したサービス以外へは送らない");
        result.Should().BeEquivalentTo(new McpToolResult(
        [
            new McpToolDocument("d1", "本文あり", new Dictionary<string, string> { ["doc_scope"] = "organization" }, "本文", "https://wiki/d1"),
            new McpToolDocument("d2", "本文なし", new Dictionary<string, string>(), null, null),
        ], 2, true), o => o.WithStrictOrdering());
        log.OfLevel(LogLevel.Warning).Should().BeEmpty();
        log.OfLevel(LogLevel.Error).Should().BeEmpty();

        // 対: もう一方のサービスが申告したツールは、もう一方へだけ届く（宛先を構成の並びや 1 つ目で決める実装を落とす）。
        (await invoker.InvokeAsync(Tool("svc-b"), Scope(), "{}", Ct)).TotalCount.Should().Be(99);
        other.Received.Should().ContainSingle();
        declaring.Received.Should().ContainSingle("svc-b のツールは svc-a へ届かない");
    }

    // 🔴 E-2（#1516 の中心）: **旧い申告元**が申告に URL を載せても（REST の `endpoint`・gRPC の番号 4）、
    // その URL へは**接続を 1 本も張らない**。実行は申告したサービスの gRPC アドレスへ届く。
    // 申告の URL には 127.0.0.1 の待受を置き、受け付け待ちの接続（`Pending`）が無いことで dial されなかったことを測る。
    [Fact]
    public async Task Legacy_declared_endpoint_is_never_dialled()
    {
        using var declaredUrl = new TcpListener(IPAddress.Loopback, 0);
        declaredUrl.Start();
        var legacyEndpoint = $"http://127.0.0.1:{((IPEndPoint)declaredUrl.LocalEndpoint).Port}/internal/mcp/tool";
        var execution = new McpToolDeclarationGrpcTestHost.StubExecution();
        await using var host = await McpToolDeclarationGrpcTestHost.StartAsync(
            ct: Ct, execution: execution, legacyEndpoint: legacyEndpoint);

        // 申告を REST と gRPC の両方で旧い形のまま集め、公開構成と突き合わせる（本番の収集器と突合のまま）。
        var collectorConfig = Config(new Dictionary<string, string?>
        {
            ["Mcp:Services:rest"] = host.HttpAddress,
            ["Mcp:GrpcServices:grpc"] = host.GrpcAddress,
        });
        var http = new HttpToolDeclarationSource(new PlainClientFactory(), collectorConfig, NullLogger<HttpToolDeclarationSource>.Instance);
        var grpc = new GrpcToolDeclarationCollector(
            new FixedTokenProvider(McpToolDeclarationGrpcTestHost.ServiceToken()), new PlainClientFactory(),
            NullLogger<GrpcToolDeclarationCollector>.Instance);
        var declarations = await new ToolDeclarationSource(http, collectorConfig, grpc).CollectAsync(Ct);
        var catalog = new ToolCatalog(NullLogger<ToolCatalog>.Instance);
        catalog.Refresh(new ToolPublicationConfig("test",
        [
            new ToolPublicationEntry(McpToolDeclarationGrpcTestHost.SampleTool.Name, McpToolDeclarationGrpcTestHost.RestService, "via-rest"),
            new ToolPublicationEntry(McpToolDeclarationGrpcTestHost.SampleTool.Name, McpToolDeclarationGrpcTestHost.GrpcService, "via-grpc"),
        ]), declarations);
        catalog.PublishedTools.Select(t => t.PublishedName).Should().BeEquivalentTo(
            ["via-rest", "via-grpc"], "旧い申告元の申告も公開の突合には使える（申告なしにしない）");

        var (invoker, _) = Build(new Dictionary<string, string?>
        {
            [$"Mcp:GrpcServices:{McpToolDeclarationGrpcTestHost.RestService}"] = host.GrpcAddress,
            [$"Mcp:GrpcServices:{McpToolDeclarationGrpcTestHost.GrpcService}"] = host.GrpcAddress,
            ["Mcp:ToolExecutionTimeoutSeconds"] = "2",
        });
        foreach (var tool in catalog.PublishedTools)
            await invoker.InvokeAsync(tool, Scope(), "{}", Ct);

        execution.Received.Should().HaveCount(2, "実行は申告したサービスの gRPC アドレスへ届く");
        declaredUrl.Pending().Should().BeFalse("🔴 申告に載っていた URL へ接続を張ってはならない");
        declaredUrl.Stop();
    }

    // 🔴 E-3: **実行口の無い宛先**（本番の申告元は #1611 までこれ）は `UNIMPLEMENTED` —— fail-closed の拒否。
    // 配線の誤りではないので Warning（Error にしない）。
    [Fact]
    public async Task Missing_execution_port_fails_closed_with_a_clear_message()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(ct: Ct);
        var (invoker, log) = Build(new Dictionary<string, string?> { ["Mcp:GrpcServices:svc"] = target.GrpcAddress });

        var act = () => invoker.InvokeAsync(Tool("svc"), Scope(), "{}", Ct);

        (await act.Should().ThrowAsync<ToolExecutionUnavailableException>())
            .Which.Message.Should().Be(GrpcToolInvoker.NoExecutionPortMessage);
        log.OfLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain("no tool execution port");
        log.OfLevel(LogLevel.Error).Should().BeEmpty();
    }

    // 🔴 E-4: 申告したサービスの gRPC アドレスが構成に無ければ、どこへも送らず拒否する。
    // 利用者へ返す文言には内部の宛先（サービス名・アドレス）を含めない。
    [Fact]
    public async Task Unrouted_service_fails_closed_without_dialling_anything()
    {
        var (invoker, log) = Build(new Dictionary<string, string?> { ["Mcp:GrpcServices:other"] = DeadAddress() });

        var act = () => invoker.InvokeAsync(Tool("svc-without-address"), Scope(), "{}", Ct);

        var thrown = (await act.Should().ThrowAsync<ToolExecutionUnavailableException>()).Which;
        thrown.Message.Should().Be(GrpcToolInvoker.NotRoutedMessage);
        thrown.Message.Should().NotContain("svc-without-address").And.NotContain("127.0.0.1");
        log.OfLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain("svc-without-address");
    }

    // 🔴 E-5: 期限は `Mcp:ToolExecutionTimeoutSeconds`。何も返さない実行面でも期限で打ち切り、拒否にする。
    [Fact]
    public async Task Deadline_from_configuration_ends_a_silent_execution()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(ct: Ct, execution: Hanging());
        var (invoker, log) = Build(new Dictionary<string, string?>
        {
            ["Mcp:GrpcServices:svc"] = target.GrpcAddress,
            ["Mcp:ToolExecutionTimeoutSeconds"] = "1",
        });

        // 期限が効かない実装では返らない。見張り（期限の 15 倍）が先に鳴ったら落とす（所要時間の判定ではない）。
        var invoke = invoker.InvokeAsync(Tool("svc"), Scope(), "{}", Ct);
        var guard = Task.Delay(TimeSpan.FromSeconds(15), Ct);
        (await Task.WhenAny(invoke, guard)).Should().BeSameAs(invoke, "期限（1 秒）で打ち切られていない");

        var act = () => invoke;
        (await act.Should().ThrowAsync<ToolExecutionUnavailableException>())
            .Which.Message.Should().Be(GrpcToolInvoker.TimedOutMessage);
        log.OfLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain("no response within");
    }

    // E-6: 呼び出し側の取り消しは拒否へ畳まず、OperationCanceledException で外へ出す（期限とは別物）。
    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_failing_closed()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(ct: Ct, execution: Hanging());
        var (invoker, log) = Build(new Dictionary<string, string?>
        {
            ["Mcp:GrpcServices:svc"] = target.GrpcAddress,
            ["Mcp:ToolExecutionTimeoutSeconds"] = "30",
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var act = () => invoker.InvokeAsync(Tool("svc"), Scope(), "{}", cts.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().NotBeOfType<ToolExecutionUnavailableException>();
        log.OfLevel(LogLevel.Warning).Should().BeEmpty();
    }

    // 🔴 E-7: s2s の配線不備（`platform-service` を持たないトークン）は拒否・**Error**（一過性の不達と混ぜない）。
    [Fact]
    public async Task Rejected_service_token_fails_closed_and_is_logged_as_error()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(
            ct: Ct, execution: new McpToolDeclarationGrpcTestHost.StubExecution());
        var (invoker, log) = Build(
            new Dictionary<string, string?> { ["Mcp:GrpcServices:svc"] = target.GrpcAddress },
            new FixedTokenProvider(McpToolDeclarationGrpcTestHost.IssueToken("service-account-mcp-server", [])));

        var act = () => invoker.InvokeAsync(Tool("svc"), Scope(), "{}", Ct);

        (await act.Should().ThrowAsync<ToolExecutionUnavailableException>())
            .Which.Message.Should().Be(GrpcToolInvoker.RejectedMessage);
        log.OfLevel(LogLevel.Error).Should().ContainSingle().Which.Message.Should().Contain("PermissionDenied");
    }

    // 🔴 E-8: s2s トークンの取得失敗も配線不備 —— 拒否・**Error**。
    [Fact]
    public async Task Service_token_acquisition_failure_fails_closed_and_is_logged_as_error()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(
            ct: Ct, execution: new McpToolDeclarationGrpcTestHost.StubExecution());
        var (invoker, log) = Build(
            new Dictionary<string, string?> { ["Mcp:GrpcServices:svc"] = target.GrpcAddress },
            new ThrowingTokenProvider());

        var act = () => invoker.InvokeAsync(Tool("svc"), Scope(), "{}", Ct);

        (await act.Should().ThrowAsync<ToolExecutionUnavailableException>())
            .Which.Message.Should().Be(GrpcToolInvoker.RejectedMessage);
        log.OfLevel(LogLevel.Error).Should().ContainSingle().Which.Message.Should().Contain("service token");
        log.OfLevel(LogLevel.Warning).Should().BeEmpty();
    }

    // E-9: 何も待ち受けていない宛先は拒否（Warning）。Error へは上げない（E-7 の対照）。
    [Fact]
    public async Task Unreachable_target_fails_closed_with_a_warning()
    {
        var (invoker, log) = Build(new Dictionary<string, string?>
        {
            ["Mcp:GrpcServices:svc"] = DeadAddress(),
            ["Mcp:ToolExecutionTimeoutSeconds"] = "5",
        });

        var act = () => invoker.InvokeAsync(Tool("svc"), Scope(), "{}", Ct);

        (await act.Should().ThrowAsync<ToolExecutionUnavailableException>())
            .Which.Message.Should().Be(GrpcToolInvoker.UnreachableMessage);
        log.OfLevel(LogLevel.Error).Should().BeEmpty();
        log.OfLevel(LogLevel.Warning).Should().ContainSingle();
    }

    // E-10: 要求の組み立て。申告名・引数（空は空のオブジェクト）・現行の実行スコープ（除外制約と属性を含む）を運ぶ。
    // 宛先の情報（URL）は要求に 1 バイトも載らない。
    [Fact]
    public void Request_carries_the_declared_name_arguments_and_current_scope()
    {
        var request = GrpcToolInvoker.ToRequest(Tool("svc", publishedName: "search"), Scope(excludePrivateNote: true), "");

        request.Tool.Should().Be(McpToolDeclarationGrpcTestHost.SampleTool.Name);
        request.ArgumentsJson.Should().Be("{}");
        request.Scope.SubjectId.Should().Be("alice");
        request.Scope.SubjectKind.Should().Be("ServiceAccount");
        request.Scope.ExcludePrivateNote.Should().BeTrue("ADR-0034 決定 9 の要求側の 1 層目を落とさない");
        request.Scope.RequiredScope.Should().Be(McpToolDeclarationGrpcTestHost.SampleTool.RequiredScope);
        request.Scope.SubjectAttributes.Should().Contain("clearance", "internal");
        Pb.ExecuteMcpToolRequest.Descriptor.Fields.InDeclarationOrder().Select(f => f.Name)
            .Should().Equal(["tool", "arguments_json", "scope"], "宛先を運ぶ項目を持たない");
    }

    // E-11: 応答の proto は共通エンベロープ（REST の JSON）と項目名・数が一致する（片方だけに足すと輸送ごとに意味が割れる）。
    [Fact]
    public void Result_proto_matches_the_envelope_wire_names()
    {
        static IReadOnlyList<string> JsonNames(Type type) =>
            [.. type.GetProperties()
                .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
                .OfType<string>()];

        Pb.McpToolResult.Descriptor.Fields.InDeclarationOrder().Select(f => f.Name)
            .Should().BeEquivalentTo(JsonNames(typeof(McpToolResult)));
        Pb.McpToolDocument.Descriptor.Fields.InDeclarationOrder().Select(f => f.Name)
            .Should().BeEquivalentTo(JsonNames(typeof(McpToolDocument)));
    }

    // E-12: 期限の構成。既定 30 秒・1 未満は 1 秒。本番の構成ファイルに既定値で並んでいる。
    [Theory]
    [InlineData(null, 30)]
    [InlineData("5", 5)]
    [InlineData("0", 1)]
    public void Timeout_is_configured_with_default_and_floor(string? configured, int expectedSeconds)
    {
        GrpcToolInvoker.ConfiguredTimeout(Config(new Dictionary<string, string?> { [GrpcToolInvoker.TimeoutKey] = configured }))
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Production_settings_declare_the_execution_timeout_with_its_default()
    {
        using var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            McpToolsGrpcDeploymentWiringTests.ReadRepoFile(McpToolsGrpcDeploymentWiringTests.AppSettings)));
        var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();

        configuration[ToolCatalogRefresher.IntervalKey].Should().NotBeNull("対照: 構成ファイルを読めていないなら以下は何も検査していない");
        configuration.GetValue<int?>(GrpcToolInvoker.TimeoutKey).Should().Be(GrpcToolInvoker.DefaultTimeoutSeconds);
    }

    // E-13: 登録。gRPC の宛先が無い配備でも実行器は組め、実行は経路なしとして拒否する（s2s の資格情報を要求しない）。
    [Fact]
    public async Task Without_grpc_targets_the_invoker_is_registered_and_fails_closed()
    {
        var configuration = Config(new Dictionary<string, string?> { ["Mcp:Services:svc"] = "http://127.0.0.1:1" });
        using var sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton(configuration)
            .AddMcpToolDeclarationSources(configuration)
            .AddMcpToolInvoker()
            .BuildServiceProvider();

        sp.GetService<IServiceTokenProvider>().Should().BeNull();
        var invoker = sp.GetRequiredService<IToolInvoker>();
        invoker.Should().BeOfType<GrpcToolInvoker>();

        var act = () => invoker.InvokeAsync(Tool("svc"), Scope(), "{}", Ct);
        (await act.Should().ThrowAsync<ToolExecutionUnavailableException>())
            .Which.Message.Should().Be(GrpcToolInvoker.NotRoutedMessage);
    }

    // E-14: 登録。gRPC の宛先が在れば s2s の発行側と一緒に組める。宛先が在るのに発行側が無ければ**組んだ時点で落ちる**
    // （Program.cs は要求を受ける前に 1 度組む）。
    [Fact]
    public void With_grpc_targets_the_invoker_needs_the_service_token_provider()
    {
        var configuration = Config(new Dictionary<string, string?>
        {
            ["Mcp:GrpcServices:svc"] = "http://127.0.0.1:1",
            ["ServiceToken:ClientId"] = "mcp-server",
        });
        using var sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton(configuration)
            .AddMcpToolDeclarationSources(configuration)
            .AddMcpToolInvoker()
            .BuildServiceProvider();
        sp.GetRequiredService<IToolInvoker>().Should().BeOfType<GrpcToolInvoker>();

        var act = () => new GrpcToolInvoker(configuration, NullLogger<GrpcToolInvoker>.Instance);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Mcp:GrpcServices*");
    }

    private sealed class PlainClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(10) };
    }
}
