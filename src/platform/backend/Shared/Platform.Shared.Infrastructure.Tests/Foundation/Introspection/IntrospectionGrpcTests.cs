using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Tests.Testing;
using Pb = Platform.Shared.Contracts.Grpc.Introspection.V1;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Introspection;

// FR-15, NFR-09, NFR-16, ADR-0018, ADR-0029, ADR-0075, IADR-0029, IADR-0379 決定 3・4・5, IADR-0462
// (#1514, #1255 経路 ⑤): 自己申告の gRPC 面（`platform.introspection.v1.ServiceIntrospection/Get`）と、
// 宛先ごとに輸送を選ぶ収集器（`EffectiveConfigCollector`）を**ループバックの実 Kestrel**で往復させる。
//
// 陽性対照（s2s で取れる・REST と同じ）と陰性対照（トークン無し・利用者トークン）を同じ器で対にする ——
// 「拒否された」だけでは器が壊れているのか認可が効いているのか区別できない。
[Trait("TestKind", "Integration")]
public sealed class IntrospectionGrpcTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    // ── 受け口 ─────────────────────────────────────────────────────────────

    // T-01: 陽性対照。s2s トークンを CallCredentials で付けた h2c チャネルで取れば、
    // **REST と同じ 1 つの申告**が返る（申告を輸送ごとに 2 つ持たない）。
    [Fact]
    public async Task Get_over_h2c_with_service_token_returns_the_same_report_as_rest()
    {
        await using var host = await IntrospectionGrpcTestHost.StartAsync(ct: Ct);
        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            host.GrpcAddress, new FixedTokenProvider(IntrospectionGrpcTestHost.ServiceToken()));
        var client = new Pb.ServiceIntrospection.ServiceIntrospectionClient(channel);

        var grpc = IntrospectionGrpcMapping.ToDto(
            await client.GetAsync(new Pb.GetServiceIntrospectionRequest(), cancellationToken: Ct));

        using var http = new HttpClient();
        var rest = await http.GetFromJsonAsync<ServiceIntrospectionDto>(
            host.HttpAddress + IntrospectionExtensions.IntrospectionPath, Ct);

        rest.Should().NotBeNull();
        grpc.Should().BeEquivalentTo(rest!, o => o.WithStrictOrdering());
        grpc.Service.Should().Be("probe-service");
        grpc.Connectors.Should().HaveCount(2, "対照: 申告が空のまま一致しているのではない");
    }

    // 🔴 T-02: **`target` の presence。** 「接続先を申告しない」（null）と「空文字の接続先」が
    // 輸送を跨いでも区別される。素の string で運ぶと null が "" に化ける（例外は 1 つも起きない）。
    [Fact]
    public async Task Port_target_null_and_empty_survive_the_round_trip()
    {
        await using var host = await IntrospectionGrpcTestHost.StartAsync(ct: Ct);
        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            host.GrpcAddress, new FixedTokenProvider(IntrospectionGrpcTestHost.ServiceToken()));
        var client = new Pb.ServiceIntrospection.ServiceIntrospectionClient(channel);

        var report = await client.GetAsync(new Pb.GetServiceIntrospectionRequest(), cancellationToken: Ct);

        report.Ports.Single(p => p.Port == "llm").HasTarget.Should().BeFalse("申告しない接続先は未設定のまま運ぶ");
        report.Ports.Single(p => p.Port == "wiki-sync").HasTarget.Should().BeTrue("空文字は値として運ぶ");

        var dto = IntrospectionGrpcMapping.ToDto(report);
        dto.Ports.Single(p => p.Port == "llm").Target.Should().BeNull();
        dto.Ports.Single(p => p.Port == "wiki-sync").Target.Should().BeEmpty();
        dto.Ports.Single(p => p.Port == "vector-store").Target.Should().Be("qdrant:6334");
    }

    // T-03: 陰性対照。トークンを付けなければ UNAUTHENTICATED（REST の面は認証を持たないが、gRPC 面は狭い）。
    [Fact]
    public async Task Get_without_token_is_unauthenticated()
    {
        await using var host = await IntrospectionGrpcTestHost.StartAsync(ct: Ct);
        using var channel = GrpcChannel.ForAddress(host.GrpcAddress);
        var client = new Pb.ServiceIntrospection.ServiceIntrospectionClient(channel);

        var act = async () => await client.GetAsync(new Pb.GetServiceIntrospectionRequest(), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    // 🔴 T-04: 陰性対照。**利用者のトークンは管理者であっても通らない**（IADR-0379 決定 4。confused deputy の防止）。
    [Fact]
    public async Task Get_with_forwarded_admin_user_token_is_permission_denied()
    {
        await using var host = await IntrospectionGrpcTestHost.StartAsync(ct: Ct);
        using var channel = GrpcChannel.ForAddress(host.GrpcAddress);
        var client = new Pb.ServiceIntrospection.ServiceIntrospectionClient(channel);
        var admin = IntrospectionGrpcTestHost.IssueToken(
            "alice", [PlatformAuthPolicies.AdminRole, PlatformAuthPolicies.OperatorRole]);

        var act = async () => await client.GetAsync(
            new Pb.GetServiceIntrospectionRequest(), headers: Bearer(admin), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // T-05: 写しの単体。段（steps）は `IntrospectionBuilder` の型制約のため器では組みにくいので、
    // 写しそのものを往復させて 1 対 1 を固定する（空文字の input・空の outputs・false もそのまま運ぶ）。
    [Fact]
    public void Mapping_round_trips_every_field_of_the_dto()
    {
        var dto = new ServiceIntrospectionDto(
            "conversion-service",
            [
                new StepIntrospectionDto("convert", "C.RawDocumentFetchedConsumer", "RawDocumentFetched", ["DocumentNormalized"], true),
                new StepIntrospectionDto("legacy", "C.Legacy", string.Empty, [], false),
            ],
            [new PortSelectionDto("storage", "Minio", null), new PortSelectionDto("vector", "Qdrant", "")],
            [new ConnectorDto("fs", false)]);

        IntrospectionGrpcMapping.ToDto(IntrospectionGrpcMapping.ToProto(dto))
            .Should().BeEquivalentTo(dto, o => o.WithStrictOrdering());
    }

    // ── 収集器（宛先ごとの輸送選択） ────────────────────────────────────────────

    private sealed class PlainClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static (EffectiveConfigCollector Collector, RecordingLogger<GrpcServiceIntrospectionCollector> GrpcLog)
        Build(IntrospectionOptions options, string? token = null)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(options);
        var http = new HttpEffectiveConfigCollector(
            new PlainClientFactory(), opts, NullLogger<HttpEffectiveConfigCollector>.Instance);
        var log = new RecordingLogger<GrpcServiceIntrospectionCollector>();
        var grpc = new GrpcServiceIntrospectionCollector(
            new FixedTokenProvider(token ?? IntrospectionGrpcTestHost.ServiceToken()), opts, log);
        return (new EffectiveConfigCollector(http, opts, grpc), log);
    }

    // 閉じたループバックのポート（何も待ち受けていない）。
    private static string DeadAddress()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    // T-06: gRPC の宛先と REST だけの宛先が混在しても、各々が構成どおりの輸送で集まる。
    // gRPC 側は REST の口を**死んだポート**に向けてあるので、REST へ倒れていれば到達不能になる（対照）。
    [Fact]
    public async Task Collects_each_target_over_its_configured_transport()
    {
        await using var grpcTarget = await IntrospectionGrpcTestHost.StartAsync(ct: Ct);
        await using var restTarget = await IntrospectionGrpcTestHost.StartAsync(
            new ServiceIntrospectionDto("rest-only", [], [], []), Ct);

        var (collector, _) = Build(new IntrospectionOptions
        {
            Services = new(StringComparer.Ordinal)
            {
                ["probe-service"] = DeadAddress(),
                ["rest-only"] = restTarget.HttpAddress,
            },
            GrpcServices = new(StringComparer.Ordinal) { ["probe-service"] = grpcTarget.GrpcAddress },
        });

        var result = await collector.CollectAsync(Ct);

        result.ReachableServices.Should().BeEquivalentTo(["probe-service", "rest-only"]);
        result.UnreachableServices.Should().BeEmpty();
        result.Services.Select(s => s.Service).Should().BeEquivalentTo(["probe-service", "rest-only"]);
    }

    // T-07: gRPC の宛先だけに構成された宛先（REST の口を持たない）も集まる —— 宛先の集合は 2 つの構成の和である。
    // 値が空の gRPC 項目は構成されていないものとして REST へ倒れる。
    [Fact]
    public async Task Grpc_only_targets_are_collected_and_empty_grpc_entries_fall_back_to_rest()
    {
        await using var grpcTarget = await IntrospectionGrpcTestHost.StartAsync(ct: Ct);
        await using var restTarget = await IntrospectionGrpcTestHost.StartAsync(
            new ServiceIntrospectionDto("blank-grpc", [], [], []), Ct);

        var (collector, _) = Build(new IntrospectionOptions
        {
            Services = new(StringComparer.Ordinal) { ["blank-grpc"] = restTarget.HttpAddress },
            GrpcServices = new(StringComparer.Ordinal)
            {
                ["probe-service"] = grpcTarget.GrpcAddress,
                ["blank-grpc"] = "  ",
            },
        });

        var result = await collector.CollectAsync(Ct);

        result.ReachableServices.Should().BeEquivalentTo(["probe-service", "blank-grpc"]);
    }

    // 🔴 T-08: s2s の配線不備（`platform-service` を持たないトークン）は**到達不能へ隔離**し（REST と同じ 2 値）、
    // **Error で記録する**（一過性の不達と混ぜない。再起動では直らない）。
    [Fact]
    public async Task Rejected_service_token_is_unreachable_and_logged_as_error()
    {
        await using var grpcTarget = await IntrospectionGrpcTestHost.StartAsync(ct: Ct);
        var (collector, log) = Build(
            new IntrospectionOptions
            {
                GrpcServices = new(StringComparer.Ordinal) { ["probe-service"] = grpcTarget.GrpcAddress },
            },
            token: IntrospectionGrpcTestHost.IssueToken("service-account-bff", []));

        var result = await collector.CollectAsync(Ct);

        result.UnreachableServices.Should().BeEquivalentTo(["probe-service"]);
        result.Services.Should().BeEmpty();
        log.OfLevel(LogLevel.Error).Should().ContainSingle()
            .Which.Message.Should().Contain("PermissionDenied");
    }

    // T-09: 何も待ち受けていない宛先は到達不能（Warning）。Error へは上げない（T-08 の対照）。
    [Fact]
    public async Task Unreachable_grpc_target_is_isolated_with_a_warning()
    {
        var (collector, log) = Build(new IntrospectionOptions
        {
            TimeoutSeconds = 2,
            GrpcServices = new(StringComparer.Ordinal) { ["gone"] = DeadAddress() },
        });

        var result = await collector.CollectAsync(Ct);

        result.UnreachableServices.Should().BeEquivalentTo(["gone"]);
        log.OfLevel(LogLevel.Error).Should().BeEmpty();
        log.OfLevel(LogLevel.Warning).Should().ContainSingle();
    }

    // 🔴 T-10: 空の service 名は「申告として無効」—— REST の空応答と同じく到達不能へ落とす。
    // 空文字のサービスとして集約へ入れると、どの宣言にも突合されない申告が 1 件増える。
    [Fact]
    public async Task Report_with_empty_service_name_is_unreachable()
    {
        await using var grpcTarget = await IntrospectionGrpcTestHost.StartAsync(
            new ServiceIntrospectionDto(string.Empty, [], [], []), Ct);
        var (collector, _) = Build(new IntrospectionOptions
        {
            GrpcServices = new(StringComparer.Ordinal) { ["blank"] = grpcTarget.GrpcAddress },
        });

        var result = await collector.CollectAsync(Ct);

        result.UnreachableServices.Should().BeEquivalentTo(["blank"]);
        result.Services.Should().BeEmpty();
    }

    // 🔴 T-11: 期限は REST と同じ `TimeoutSeconds` を引く。接続は受けるが何も返さない宛先でも、
    // 期限で打ち切って到達不能にする（既定の無期限だと定期検出の 1 周が止まる）。
    [Fact]
    public async Task Deadline_follows_timeout_seconds_for_a_silent_target()
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

        var (collector, _) = Build(new IntrospectionOptions
        {
            TimeoutSeconds = 1,
            GrpcServices = new(StringComparer.Ordinal)
            {
                ["silent"] = $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}",
            },
        });

        var watch = Stopwatch.StartNew();
        var result = await collector.CollectAsync(Ct);
        watch.Stop();

        result.UnreachableServices.Should().BeEquivalentTo(["silent"]);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "期限（1 秒）で打ち切られる");
        silent.Stop();
        foreach (var c in accepted) c.Dispose();
    }

    // T-12: 呼び出し側の取り消し（停止要求）は到達不能へ畳まず、OperationCanceledException で外へ出す（REST と同じ。#1382）。
    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_marking_unreachable()
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

        var (collector, _) = Build(new IntrospectionOptions
        {
            TimeoutSeconds = 30,
            GrpcServices = new(StringComparer.Ordinal)
            {
                ["silent"] = $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}",
            },
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var act = async () => await collector.CollectAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        silent.Stop();
        foreach (var c in accepted) c.Dispose();
    }

    // T-13: gRPC の宛先が構成されているのに gRPC の収集器が無いのは登録の誤り —— 黙って REST へ倒さず起動で落とす。
    [Fact]
    public void Configured_grpc_targets_without_a_grpc_collector_fail_fast()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new IntrospectionOptions
        {
            GrpcServices = new(StringComparer.Ordinal) { ["x"] = "http://x:8081" },
        });
        var http = new HttpEffectiveConfigCollector(
            new PlainClientFactory(), opts, NullLogger<HttpEffectiveConfigCollector>.Instance);

        var act = () => new EffectiveConfigCollector(http, opts);

        act.Should().Throw<InvalidOperationException>().WithMessage("*GrpcServices*");
    }

    // T-14: 対照。gRPC の宛先が無ければ gRPC の収集器が無くても組み立てられ、REST だけで集まる（既存配備は不変）。
    [Fact]
    public async Task Without_grpc_targets_the_collector_is_rest_only()
    {
        await using var restTarget = await IntrospectionGrpcTestHost.StartAsync(ct: Ct);
        var opts = Microsoft.Extensions.Options.Options.Create(new IntrospectionOptions
        {
            Services = new(StringComparer.Ordinal) { ["probe-service"] = restTarget.HttpAddress },
        });
        var http = new HttpEffectiveConfigCollector(
            new PlainClientFactory(), opts, NullLogger<HttpEffectiveConfigCollector>.Instance);

        var result = await new EffectiveConfigCollector(http, opts).CollectAsync(Ct);

        result.ReachableServices.Should().BeEquivalentTo(["probe-service"]);
    }
}
