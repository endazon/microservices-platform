using AwesomeAssertions;
using DashboardService.Domain;
using DashboardService.Features.KnowledgeHealth.Report;
using DashboardService.Tests.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Grpc.Dashboard.V1;
using Microsoft.Extensions.Configuration;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DashboardService.Tests.Features.KnowledgeHealth.Report;

// FR-10, FR-17, FR-18, NFR-16, ADR-0029, ADR-0075, [[IADR-0542]] (#1907): 観測値の受け口（H-1）の受信上限。
//
// 稼働の台帳は再索引で伸び続けている（切り替え後だけで 39,780 件）。grpc-dotnet の既定 4 MB では約 9.5 万件で
// `ResourceExhausted` になり、H-1 が毎周期失敗する。ここでは **実 Kestrel の h2c ポート**で、
//   1. 15 万件（最も重い形）の要求が 4 MB を超え、それでも受け口に受理される（陽性）
//   2. 受信上限（既定 16 MiB）を超える要求は `ResourceExhausted` で拒まれる（陰性。上限が「無制限」に化けていない）
// を対で固定する。期限（30 秒。[[IADR-0542]] 決定 2）に収まることは実 PostgreSQL の試験
// （`Knowledge.IntegrationTests` の `KnowledgeHealthSnapshotPostgresTests.十五万件の報告も受信上限と期限の両方に収まる`）が測る。
// ここは EF InMemory なので、所要は測らない。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class KnowledgeHealthReportReceiveLimitTests
{
    // 受け入れ基準の規模（#1907）。
    internal const int TargetCount = 150_000;

    // grpc-dotnet の既定の受信上限（従前の値）。
    private const int GrpcDefaultMaxReceiveMessageSize = 4 * 1024 * 1024;

    private readonly GrpcKestrelFactory _factory;

    public KnowledgeHealthReportReceiveLimitTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Metadata Bearer() => new()
    {
        {
            "Authorization",
            $"Bearer {GrpcKestrelFactory.IssueToken("service-account-graph-service", [PlatformAuthPolicies.ServiceRole])}"
        },
    };

    private KnowledgeHealthReport.KnowledgeHealthReportClient Client() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    // 🔴 陽性: 15 万件（最も重い形）が 4 MB を超え、16 MiB に収まり、受理される。
    // 従前（上限を明示しない）なら `ResourceExhausted` で落ちる規模であることもここで確かめる（サイズの下限）。
    [Fact]
    public async Task 十五万件の報告は既定の4MBを超えるが受け口に受理される()
    {
        var request = HeaviestShape(KnowledgeHealthIndicators.UnresolvedLinks, TargetCount);
        var bytes = request.CalculateSize();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"15 万件（最も重い形）の要求: {bytes:N0} バイト（1 件 {bytes / (double)TargetCount:F1} バイト）");

        bytes.Should().BeGreaterThan(GrpcDefaultMaxReceiveMessageSize, "従前の既定 4 MB では受け取れない規模である");
        bytes.Should().BeLessThan(KnowledgeHealthReportReceiveLimit.DefaultMaxReceiveMessageSize,
            "既定の受信上限に収まる");

        var response = await Client().ReportAsync(request, headers: Bearer(), cancellationToken: Ct);

        response.Accepted.Should().Be(TargetCount);
    }

    // 🔴 陰性: 受信上限を超える要求は `ResourceExhausted`。上限が無制限・全体の値へ化けていないことの対照である
    // （`AddGrpc()` より前に Configure を登録すると全体の 4 MB で上書きされ、上の陽性が落ちる。両方で挟む）。
    [Fact]
    public async Task 受信上限を超える報告はResourceExhaustedで拒まれる()
    {
        var count = KnowledgeHealthReportReceiveLimit.DefaultMaxReceiveMessageSize / 94 + 10_000;
        var request = HeaviestShape(KnowledgeHealthIndicators.UnresolvedLinks, count);
        request.CalculateSize().Should().BeGreaterThan(KnowledgeHealthReportReceiveLimit.DefaultMaxReceiveMessageSize);

        var act = async () => await Client().ReportAsync(request, headers: Bearer(), cancellationToken: Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.ResourceExhausted);
    }

    // 🔴 送信側（graph の `GrpcKnowledgeHealthReporter.PayloadWarnBytes`＝12 MiB）は本既定の 75% として書いてある。
    // **サービスを跨ぐため定数を共有できない**ので、既定値をリテラルで固定する（片側だけ動かすと警告が意味を失う）。
    [Fact]
    public void 既定の受信上限は16MiBである()
    {
        KnowledgeHealthReportReceiveLimit.DefaultMaxReceiveMessageSize.Should().Be(16 * 1024 * 1024);
    }

    [Fact]
    public void 構成が無ければ既定を使い_正の整数なら採る()
    {
        KnowledgeHealthReportReceiveLimit.Read(Config(null))
            .Should().Be(KnowledgeHealthReportReceiveLimit.DefaultMaxReceiveMessageSize);
        KnowledgeHealthReportReceiveLimit.Read(Config("   "))
            .Should().Be(KnowledgeHealthReportReceiveLimit.DefaultMaxReceiveMessageSize);
        KnowledgeHealthReportReceiveLimit.Read(Config("33554432")).Should().Be(32 * 1024 * 1024);
    }

    // 🔴 不正値は既定へ黙って戻さず落とす（#1899 の D-1 と同じ姿勢）。
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("16MB")]
    [InlineData("99999999999")]
    public void 不正な構成は構成キーを名指して落ちる(string raw)
    {
        var act = () => KnowledgeHealthReportReceiveLimit.Read(Config(raw));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{KnowledgeHealthReportReceiveLimit.ConfigKey}*");
    }

    // 最も重い形 = 解決できないリンク（graph の `UnresolvedLinkKey`：文書 ID の N 形式 32 桁 ＋ ":" ＋ 16 バイトの 16 進 32 桁）
    // に軸（not-found）と個人資料の印を全件に付けたもの。1 件 94 バイトになる。
    internal static ReportRequest HeaviestShape(string indicator, int count)
    {
        var request = new ReportRequest { Indicator = indicator };
        for (var i = 0; i < count; i++)
            request.Observations.Add(new Observation
            {
                SubjectKey = $"{Guid.NewGuid():N}:{Guid.NewGuid():N}",
                DocScope = KnowledgeDocScopes.PrivateNote,
                Dimension = "not-found",
            });
        return request;
    }

    private static IConfiguration Config(string? raw) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [KnowledgeHealthReportReceiveLimit.ConfigKey] = raw })
        .Build();
}
