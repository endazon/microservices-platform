using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Google.Protobuf;
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
// IADR-0462（2026-09-26 追記 / #1515, #1255 経路 ④-a）, [[IADR-0533]] 決定 3 (#1517): MCP のツール申告を集める収集器。
// ［2026-10-10 / #1517］REST の収集（`GET /internal/mcp-tools`）は撤去し、宛先は `Mcp:Services`（値は h2c のアドレス）へ一本化した。
//
// 申告元の代わりに**ループバック（127.0.0.1）の実 Kestrel**（`McpToolDeclarationGrpcTestHost`）を立て、
// h2c・s2s・失敗の畳み方（申告なし＝推測で公開しない）・取り消し・期限・登録を測る。
[Trait("TestKind", "Integration")]
public sealed class GrpcToolDeclarationCollectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Config(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static (ToolDeclarationSource Source, RecordingLogger<GrpcToolDeclarationCollector> GrpcLog) Build(
        IDictionary<string, string?> values, IServiceTokenProvider? tokenProvider = null, int? timeoutSeconds = null,
        RecordingLogger<ToolDeclarationSource>? sourceLog = null)
    {
        if (timeoutSeconds is { } t)
            values[GrpcToolDeclarationCollector.TimeoutKey] = t.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var configuration = Config(values);
        var log = new RecordingLogger<GrpcToolDeclarationCollector>();
        var grpc = new GrpcToolDeclarationCollector(
            tokenProvider ?? new FixedTokenProvider(McpToolDeclarationGrpcTestHost.ServiceToken()), configuration, log);
        return (new ToolDeclarationSource(grpc, configuration, sourceLog), log);
    }

    private static string DeadAddress()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    // T-G1: 陽性対照。s2s トークンの h2c で集めた申告は、5 項目とも申告元が返したとおりに McpServer の DTO へ戻る。
    [Fact]
    public async Task Collects_declarations_over_h2c_with_the_service_token()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(ct: Ct);
        var (source, log) = Build(new Dictionary<string, string?> { ["Mcp:Services:probe"] = target.GrpcAddress });

        var collected = await source.CollectAsync(Ct);

        collected.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ServiceToolDeclarations(McpToolDeclarationGrpcTestHost.DefaultService, [McpToolDeclarationGrpcTestHost.SampleTool]),
            o => o.WithStrictOrdering());
        log.OfLevel(LogLevel.Error).Should().BeEmpty();
        log.OfLevel(LogLevel.Warning).Should().BeEmpty();
    }

    // 🔴 T-G2: 宛先はすべて gRPC で集める（構成の順序を保つ）。🔴 宛先を HTTP/1.1 の口（旧 REST の宛先 `:8080` 相当）へ
    // 向けると申告なしになる —— 移し忘れた上書き値が「申告なし」として現れることの観測点（REST へ倒れて集まる形には戻らない）。
    // 空のアドレスは構成されていないものとして宛先に入れない。
    [Fact]
    public async Task Collects_every_target_over_grpc_and_a_rest_address_is_no_declaration()
    {
        await using var first = await McpToolDeclarationGrpcTestHost.StartAsync("a-first", Ct);
        await using var second = await McpToolDeclarationGrpcTestHost.StartAsync("b-second", Ct);
        await using var restAddressed = await McpToolDeclarationGrpcTestHost.StartAsync("c-rest", Ct);
        var (source, _) = Build(new Dictionary<string, string?>
        {
            ["Mcp:Services:a-first"] = first.GrpcAddress,
            ["Mcp:Services:b-second"] = second.GrpcAddress,
            ["Mcp:Services:c-rest"] = restAddressed.HttpAddress,
            ["Mcp:Services:d-blank"] = "",
        }, timeoutSeconds: 2);

        var collected = await source.CollectAsync(Ct);

        collected.Select(d => d.Service).Should().Equal("a-first", "b-second");
        (first.RestHits, first.GrpcHits).Should().Be((0, 1));
        (second.RestHits, second.GrpcHits).Should().Be((0, 1));
        restAddressed.RestHits.Should().Be(0, "REST の口は呼ばない（撤去済み）");
        collected.Should().AllSatisfy(d => d.Tools.Should().ContainSingle()
            .Which.Should().Be(McpToolDeclarationGrpcTestHost.SampleTool));
    }

    // 🔴 T-G18（#1516 監査 M-1, ADR-0117 決定 1）: **封筒の `service` が収集先のキーと違う申告は拒否する**。
    // キー `victim-a` / `victim-b` で集めたホストが別のサービス（`spoofed`）を名乗っても、その名では 1 件も集まらず、Error で記録される。
    // 名乗りを偽らない他の宛先（`honest`）の申告は集まる。
    [Fact]
    public async Task Declarations_naming_another_service_than_the_target_are_rejected()
    {
        await using var spoofRest = await McpToolDeclarationGrpcTestHost.StartAsync("spoofed", Ct);
        await using var spoofGrpc = await McpToolDeclarationGrpcTestHost.StartAsync("spoofed", Ct);
        await using var honest = await McpToolDeclarationGrpcTestHost.StartAsync("honest", Ct);
        var sourceLog = new RecordingLogger<ToolDeclarationSource>();
        var (source, _) = Build(new Dictionary<string, string?>
        {
            ["Mcp:Services:victim-a"] = spoofRest.GrpcAddress,
            ["Mcp:Services:victim-b"] = spoofGrpc.GrpcAddress,
            ["Mcp:Services:honest"] = honest.GrpcAddress,
        }, sourceLog: sourceLog);

        var collected = await source.CollectAsync(Ct);

        collected.Select(d => d.Service).Should().Equal(["honest"], "名乗りを偽った 2 宛先は申告なし。偽らない宛先は集まる");
        (spoofRest.GrpcHits, spoofGrpc.GrpcHits).Should().Be((1, 1), "対照: 偽った宛先にも実際に問い合わせている");
        sourceLog.OfLevel(LogLevel.Error).Should().HaveCount(2).And.AllSatisfy(e => e.Message.Should().Contain("spoofed"));
    }

    // 🔴 T-G3: s2s の配線不備（`platform-service` を持たないトークン）は**申告なし**へ畳み（推測で公開しない）、
    // **Error で記録する**（一過性の不達と混ぜない。再起動では直らない）。
    [Fact]
    public async Task Rejected_service_token_is_no_declaration_and_logged_as_error()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(ct: Ct);
        var (source, log) = Build(
            new Dictionary<string, string?> { ["Mcp:Services:probe"] = target.GrpcAddress },
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
            new Dictionary<string, string?> { ["Mcp:Services:probe"] = target.GrpcAddress },
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
            new Dictionary<string, string?> { ["Mcp:Services:probe"] = target.GrpcAddress },
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
                ["Mcp:Services:gone"] = DeadAddress(),
                ["Mcp:Services:probe"] = target.GrpcAddress,
            },
            timeoutSeconds: 5);

        var collected = await source.CollectAsync(Ct);

        collected.Select(d => d.Service).Should().Equal(McpToolDeclarationGrpcTestHost.DefaultService);
        log.OfLevel(LogLevel.Error).Should().BeEmpty();
        log.OfLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain("gone");
    }

    // 🔴 T-G7: 空の service 名は「申告として無効」—— 申告なしへ落とす（どの公開構成にも一致しない申告を入れない）。
    [Fact]
    public async Task Declarations_with_empty_service_name_are_no_declaration()
    {
        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync("blank", Ct, grpcService: string.Empty);
        var (source, log) = Build(new Dictionary<string, string?> { ["Mcp:Services:blank"] = target.GrpcAddress });

        var collected = await source.CollectAsync(Ct);

        collected.Should().BeEmpty();
        log.OfLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain("empty service name");
    }

    // 🔴 T-G8: 期限は `Mcp:DeclarationTimeoutSeconds` を引く。接続は受けるが何も返さない宛先でも期限で打ち切り、申告なしにする
    // （無期限だと収集の 1 周が止まり、公開構成の突合も止まる）。
    [Fact]
    public async Task Deadline_follows_the_declaration_timeout_for_a_silent_target()
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
                ["Mcp:Services:silent"] = $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}",
            },
            timeoutSeconds: 1);

        // 🔴 期限が効かない実装では収集が返らない。試験ごと止まらないよう期限（1 秒）の 15 倍の見張りを置き、
        // 見張りが先に鳴ったら「期限で打ち切られなかった」として落とす（所要時間の判定ではない）。
        var collect = source.CollectAsync(Ct);
        var guard = Task.Delay(TimeSpan.FromSeconds(15), Ct);
        (await Task.WhenAny(collect, guard)).Should().BeSameAs(collect,
            "期限（Mcp:DeclarationTimeoutSeconds=1）で打ち切られていない —— 何も返さない宛先で収集が止まったままである");

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
                ["Mcp:Services:silent"] = $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}",
            },
            timeoutSeconds: 30);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var act = async () => await source.CollectAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        silent.Stop();
        foreach (var c in accepted) c.Dispose();
    }

    // 🔴 T-G10: 撤去した旧キー `Mcp:GrpcServices` が残っていれば**起動時に落とす**（黙って無視すると、移し忘れた
    // 上書き値がツールの静かな消失としてしか現れない）。値が空でも落とす（キーが残っていること自体が移し忘れである）。
    [Theory]
    [InlineData("http://127.0.0.1:1")]
    [InlineData("")]
    public void Retired_grpc_services_key_fails_fast(string value)
    {
        var configuration = Config(new Dictionary<string, string?>
        {
            ["Mcp:Services:probe"] = "http://127.0.0.1:1",
            ["Mcp:GrpcServices:probe"] = value,
        });

        var act = () => new ServiceCollection().AddMcpToolDeclarationSources(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Mcp:GrpcServices*Mcp:Services*probe*");
    }

    // T-G12: 登録。gRPC の収集器と s2s の発行側が常に登録され、収集器が組める（起動時に落ちない）。
    [Fact]
    public void The_grpc_collector_and_service_token_are_always_registered()
    {
        var configuration = Config(new Dictionary<string, string?>
        {
            ["Mcp:Services:probe"] = "http://127.0.0.1:1",
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

    // 🔴 T-G13: 申告の形は DTO の JSON 名（`McpToolContracts.cs`。公開構成・管理 API が使う）と proto の 2 つが在る。
    // 項目の名前と数が一致していることを固定する —— 片方だけに項目を足すと、写しで項目が落ちる。
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
            .Should().BeEmpty("引数は持たない");
    }

    // 🔴 T-G15（#1516, ADR-0117 決定 1）: **ツール定義規約は 5 項目。** 実行先の URL（旧 `endpoint`・番号 4）は
    // proto にも DTO にも無い —— 番号 4 を再び使うと、旧い申告元の URL が新しい受け手に読まれる。
    [Fact]
    public void Declaration_carries_no_endpoint_and_field_4_is_not_reused()
    {
        Pb.McpToolDeclaration.Descriptor.Fields.InDeclarationOrder().Select(f => f.Name)
            .Should().Equal(["name", "description", "input_schema", "required_scope", "egress_class"]);
        Pb.McpToolDeclaration.Descriptor.FindFieldByNumber(4).Should().BeNull("番号 4 は reserved（旧 endpoint）");
        typeof(McpToolDeclaration).GetProperties().Select(p => p.Name).Should().NotContain("Endpoint");
    }

    // 🔴 T-G16（#1516）: **旧い申告元**（gRPC の番号 4 に URL を載せる）の申告は、番号 4 を読み飛ばして 5 項目だけが戻る。
    // 申告の中身は変わらず公開の突合に使える（旧い申告元を「申告なし」にしない）。
    [Fact]
    public async Task Legacy_grpc_declaration_with_field_4_is_collected_without_the_endpoint()
    {
        const string legacyEndpoint = "http://127.0.0.1:9/internal/mcp/tool";
        // 自己確認: 代役は本当に番号 4 をワイヤへ載せている（載っていなければ本試験は何も測らない）。
        McpToolDeclarationGrpcTestHost.LegacyGrpcTool(legacyEndpoint).ToByteArray()
            .Should().ContainInConsecutiveOrder(System.Text.Encoding.UTF8.GetBytes(legacyEndpoint));

        await using var target = await McpToolDeclarationGrpcTestHost.StartAsync(ct: Ct, legacyEndpoint: legacyEndpoint);
        var (source, log) = Build(new Dictionary<string, string?> { ["Mcp:Services:probe"] = target.GrpcAddress });

        var collected = await source.CollectAsync(Ct);

        collected.Should().ContainSingle().Which.Tools.Should().ContainSingle()
            .Which.Should().Be(McpToolDeclarationGrpcTestHost.SampleTool);
        log.OfLevel(LogLevel.Warning).Should().BeEmpty();
    }

    // T-G14: 写しは 5 項目を落とさない（各項目に別の値を入れて往復させる）。
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
            RequiredScope = tool.RequiredScope,
            EgressClass = tool.EgressClass,
        });

        GrpcToolDeclarationCollector.ToDto(message).Should().BeEquivalentTo(
            new ServiceToolDeclarations("svc", [tool]), o => o.WithStrictOrdering());
    }
}
