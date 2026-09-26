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

// FR-16, NFR-09, NFR-16, ADR-0024 §2・§5, ADR-0029, ADR-0075, IADR-0379 決定 4・5,
// IADR-0462（2026-09-26 追記 / #1515, #1255 経路 ④-a）: MCP のツール申告を宛先ごとに輸送を選んで集める収集器。
//
// 申告元の代わりに**ループバック（127.0.0.1）の実 Kestrel**（`McpToolDeclarationGrpcTestHost`）を立て、
// h2c・s2s・宛先ごとの輸送選択・失敗の畳み方（申告なし＝推測で公開しない）・取り消し・期限・登録を測る。
[Trait("TestKind", "Integration")]
public sealed class GrpcToolDeclarationCollectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 名前付きクライアントの Timeout だけを差し替えられる HttpClient の供給元（REST の期限 = gRPC の期限）。
    private sealed class TimeoutClientFactory(TimeSpan? timeout = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient();
            if (timeout is { } t) client.Timeout = t;
            return client;
        }
    }

    private static IConfiguration Config(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static (ToolDeclarationSource Source, RecordingLogger<GrpcToolDeclarationCollector> GrpcLog) Build(
        IDictionary<string, string?> values, IServiceTokenProvider? tokenProvider = null, TimeSpan? timeout = null)
    {
        var configuration = Config(values);
        var factory = new TimeoutClientFactory(timeout);
        var http = new HttpToolDeclarationSource(factory, configuration, NullLogger<HttpToolDeclarationSource>.Instance);
        var log = new RecordingLogger<GrpcToolDeclarationCollector>();
        var grpc = new GrpcToolDeclarationCollector(
            tokenProvider ?? new FixedTokenProvider(McpToolDeclarationGrpcTestHost.ServiceToken()), factory, log);
        return (new ToolDeclarationSource(http, configuration, grpc), log);
    }

    private static string DeadAddress()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    // T-G1: 陽性対照。s2s トークンの h2c で集めた申告は、6 項目とも申告元が返したとおりに McpServer の DTO へ戻る。
    [Fact]
    public async Task Collects_declarations_over_h2c_with_the_service_token()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(ct: Ct);
        var (source, log) = Build(new Dictionary<string, string?> { ["Mcp:GrpcServices:probe"] = target.GrpcAddress });

        var collected = await source.CollectAsync(Ct);

        collected.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ServiceToolDeclarations(McpToolDeclarationGrpcTestHost.GrpcService, [McpToolDeclarationGrpcTestHost.SampleTool]),
            o => o.WithStrictOrdering());
        log.OfLevel(LogLevel.Error).Should().BeEmpty();
        log.OfLevel(LogLevel.Warning).Should().BeEmpty();
    }

    // 🔴 T-G2: 宛先ごとに輸送を選ぶ。宛先 = `Mcp:Services` と `Mcp:GrpcServices` のキーの和（構成の順序を保つ）。
    // gRPC のアドレスが在る宛先だけが gRPC（両方に在れば gRPC）、空のアドレスは構成されていないものとして REST のまま。
    [Fact]
    public async Task Collects_each_target_over_its_configured_transport()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(ct: Ct);
        var (source, _) = Build(new Dictionary<string, string?>
        {
            ["Mcp:Services:rest-only"] = target.HttpAddress,
            ["Mcp:Services:both"] = target.HttpAddress,
            ["Mcp:Services:blank-grpc"] = target.HttpAddress,
            ["Mcp:GrpcServices:both"] = target.GrpcAddress,
            ["Mcp:GrpcServices:blank-grpc"] = "",
            ["Mcp:GrpcServices:grpc-only"] = target.GrpcAddress,
        });

        var collected = await source.CollectAsync(Ct);

        collected.Select(d => d.Service).Should().Equal(
            McpToolDeclarationGrpcTestHost.RestService,   // rest-only
            McpToolDeclarationGrpcTestHost.GrpcService,   // both（gRPC が勝つ）
            McpToolDeclarationGrpcTestHost.RestService,   // blank-grpc（空は構成されていない）
            McpToolDeclarationGrpcTestHost.GrpcService);  // grpc-only
        collected.Should().AllSatisfy(d => d.Tools.Should().ContainSingle()
            .Which.Should().Be(McpToolDeclarationGrpcTestHost.SampleTool, "輸送を替えても申告の中身は同じ"));
    }

    // 🔴 T-G3: s2s の配線不備（`platform-service` を持たないトークン）は**申告なし**へ畳み（推測で公開しない）、
    // **Error で記録する**（一過性の不達と混ぜない。再起動では直らない）。
    [Fact]
    public async Task Rejected_service_token_is_no_declaration_and_logged_as_error()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(ct: Ct);
        var (source, log) = Build(
            new Dictionary<string, string?> { ["Mcp:GrpcServices:probe"] = target.GrpcAddress },
            new FixedTokenProvider(McpToolDeclarationGrpcTestHost.IssueToken("service-account-mcp-server", [])));

        var collected = await source.CollectAsync(Ct);

        collected.Should().BeEmpty();
        log.OfLevel(LogLevel.Error).Should().ContainSingle().Which.Message.Should().Contain("PermissionDenied");
    }

    // T-G4: 検証できないトークン（署名違い）は UNAUTHENTICATED —— 同じく申告なし・Error。
    [Fact]
    public async Task Unauthenticated_is_no_declaration_and_logged_as_error()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(ct: Ct);
        var (source, log) = Build(
            new Dictionary<string, string?> { ["Mcp:GrpcServices:probe"] = target.GrpcAddress },
            new FixedTokenProvider("not-a-jwt"));

        var collected = await source.CollectAsync(Ct);

        collected.Should().BeEmpty();
        log.OfLevel(LogLevel.Error).Should().ContainSingle().Which.Message.Should().Contain("Unauthenticated");
    }

    // 🔴 T-G5: s2s トークンの取得失敗（`ServiceToken:ClientId` の注入漏れ等）も配線不備 —— 申告なし・**Error**。
    [Fact]
    public async Task Service_token_acquisition_failure_is_no_declaration_and_logged_as_error()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(ct: Ct);
        var (source, log) = Build(
            new Dictionary<string, string?> { ["Mcp:GrpcServices:probe"] = target.GrpcAddress },
            new ThrowingTokenProvider());

        var collected = await source.CollectAsync(Ct);

        collected.Should().BeEmpty();
        log.OfLevel(LogLevel.Error).Should().ContainSingle().Which.Message.Should().Contain("service token");
        log.OfLevel(LogLevel.Warning).Should().BeEmpty();
    }

    // T-G6: 何も待ち受けていない宛先は申告なし（Warning）。Error へは上げない（T-G3 の対照）。
    // 🔴 1 宛先の失敗で他の宛先の収集を止めない（REST と同じ）。
    [Fact]
    public async Task Unreachable_grpc_target_is_no_declaration_with_a_warning_and_others_are_still_collected()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(ct: Ct);
        var (source, log) = Build(
            new Dictionary<string, string?>
            {
                ["Mcp:GrpcServices:gone"] = DeadAddress(),
                ["Mcp:GrpcServices:probe"] = target.GrpcAddress,
            },
            timeout: TimeSpan.FromSeconds(5));

        var collected = await source.CollectAsync(Ct);

        collected.Select(d => d.Service).Should().Equal(McpToolDeclarationGrpcTestHost.GrpcService);
        log.OfLevel(LogLevel.Error).Should().BeEmpty();
        log.OfLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain("gone");
    }

    // 🔴 T-G7: 空の service 名は「申告として無効」—— 申告なしへ落とす（どの公開構成にも一致しない申告を入れない）。
    [Fact]
    public async Task Declarations_with_empty_service_name_are_no_declaration()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(grpcService: string.Empty, ct: Ct);
        var (source, log) = Build(new Dictionary<string, string?> { ["Mcp:GrpcServices:blank"] = target.GrpcAddress });

        var collected = await source.CollectAsync(Ct);

        collected.Should().BeEmpty();
        log.OfLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain("empty service name");
    }

    // 🔴 T-G8: 期限は REST の HttpClient の Timeout を引く。接続は受けるが何も返さない宛先でも期限で打ち切り、申告なしにする
    // （無期限だと収集の 1 周が止まり、公開構成の突合も止まる）。
    [Fact]
    public async Task Deadline_follows_the_rest_client_timeout_for_a_silent_target()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var accepted = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true) accepted.Add(await silent.AcceptTcpClientAsync(Ct));
            }
            catch (Exception) { /* 停止 */ }
        }, Ct);

        var (source, _) = Build(
            new Dictionary<string, string?>
            {
                ["Mcp:GrpcServices:silent"] = $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}",
            },
            timeout: TimeSpan.FromSeconds(1));

        // 🔴 期限が効かない実装では収集が返らない。試験ごと止まらないよう期限（1 秒）の 15 倍の見張りを置き、
        // 見張りが先に鳴ったら「期限で打ち切られなかった」として落とす（所要時間の判定ではない）。
        var collect = source.CollectAsync(Ct);
        var guard = Task.Delay(TimeSpan.FromSeconds(15), Ct);
        (await Task.WhenAny(collect, guard)).Should().BeSameAs(collect,
            "期限（HttpClient.Timeout=1s）で打ち切られていない —— 何も返さない宛先で収集が止まったままである");

        (await collect).Should().BeEmpty();
        silent.Stop();
        foreach (var c in accepted) c.Dispose();
    }

    // T-G9: 呼び出し側の取り消し（停止要求）は申告なしへ畳まず、OperationCanceledException で外へ出す（REST と同じ）。
    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_being_no_declaration()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var accepted = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true) accepted.Add(await silent.AcceptTcpClientAsync(Ct));
            }
            catch (Exception) { /* 停止 */ }
        }, Ct);

        var (source, _) = Build(
            new Dictionary<string, string?>
            {
                ["Mcp:GrpcServices:silent"] = $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}",
            },
            timeout: TimeSpan.FromSeconds(30));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var act = async () => await source.CollectAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        silent.Stop();
        foreach (var c in accepted) c.Dispose();
    }

    // 🔴 T-G10: gRPC の宛先が構成されているのに gRPC の収集器が無ければ**起動時に落とす**
    // （黙って REST へ倒すと、REST の退役の段でツールが静かに消える）。
    [Fact]
    public void Configured_grpc_targets_without_a_grpc_collector_fail_fast()
    {
        var configuration = Config(new Dictionary<string, string?> { ["Mcp:GrpcServices:probe"] = "http://127.0.0.1:1" });
        var http = new HttpToolDeclarationSource(
            new TimeoutClientFactory(), configuration, NullLogger<HttpToolDeclarationSource>.Instance);

        var act = () => new ToolDeclarationSource(http, configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Mcp:GrpcServices*");
    }

    // T-G11: 登録。`Mcp:GrpcServices` が無い配備は gRPC の収集器も s2s の発行側も登録しない（資格情報を要求しない）。
    [Fact]
    public void Without_grpc_targets_only_rest_is_registered()
    {
        var configuration = Config(new Dictionary<string, string?> { ["Mcp:Services:probe"] = "http://127.0.0.1:1" });
        using var sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton(configuration)
            .AddMcpToolDeclarationSources(configuration)
            .BuildServiceProvider();

        sp.GetService<GrpcToolDeclarationCollector>().Should().BeNull();
        sp.GetService<IServiceTokenProvider>().Should().BeNull();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<IToolDeclarationSource>().Should().BeOfType<ToolDeclarationSource>();
    }

    // T-G12: 登録。`Mcp:GrpcServices` が在れば gRPC の収集器と s2s の発行側が登録され、収集器が組める（起動時に落ちない）。
    [Fact]
    public void With_grpc_targets_the_grpc_collector_and_service_token_are_registered()
    {
        var configuration = Config(new Dictionary<string, string?>
        {
            ["Mcp:GrpcServices:probe"] = "http://127.0.0.1:1",
            ["ServiceToken:ClientId"] = "mcp-server",
        });
        using var sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton(configuration)
            .AddMcpToolDeclarationSources(configuration)
            .BuildServiceProvider();

        sp.GetService<GrpcToolDeclarationCollector>().Should().NotBeNull();
        sp.GetService<IServiceTokenProvider>().Should().NotBeNull();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<IToolDeclarationSource>().Should().BeOfType<ToolDeclarationSource>();
    }

    // 🔴 T-G13: **並走中は申告の形が 2 つ在る**（REST の JSON = `McpToolContracts.cs`、gRPC = proto）。
    // 項目の名前と数が一致していることを固定する —— 片方だけに項目を足すと、輸送ごとに違う申告になる。
    [Fact]
    public void Proto_fields_match_the_rest_wire_names_of_the_dto()
    {
        static IReadOnlyList<string> JsonNames(Type type) =>
            [.. type.GetProperties()
                .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
                .OfType<string>()];

        Pb.McpToolDeclaration.Descriptor.Fields.InDeclarationOrder().Select(f => f.Name)
            .Should().BeEquivalentTo(JsonNames(typeof(McpToolDeclaration)));
        Pb.ServiceToolDeclarations.Descriptor.Fields.InDeclarationOrder().Select(f => f.Name)
            .Should().BeEquivalentTo(JsonNames(typeof(ServiceToolDeclarations)));
        Pb.DeclareMcpToolsRequest.Descriptor.Fields.InDeclarationOrder()
            .Should().BeEmpty("引数は持たない（REST も持たない）");
    }

    // T-G14: 写しは 6 項目を落とさない（各項目に別の値を入れて往復させる）。
    [Fact]
    public void Mapping_carries_every_field()
    {
        var tool = McpToolDeclarationGrpcTestHost.SampleTool;
        var message = new Pb.ServiceToolDeclarations { Service = "svc" };
        message.Tools.Add(new Pb.McpToolDeclaration
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
            Endpoint = tool.Endpoint,
            RequiredScope = tool.RequiredScope,
            EgressClass = tool.EgressClass,
        });

        GrpcToolDeclarationCollector.ToDto(message).Should().BeEquivalentTo(
            new ServiceToolDeclarations("svc", [tool]), o => o.WithStrictOrdering());
    }
}
