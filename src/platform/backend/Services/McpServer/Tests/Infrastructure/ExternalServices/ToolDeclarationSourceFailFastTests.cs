using AwesomeAssertions;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// 🔴 FR-16, NFR-16, IADR-0462（2026-09-26 追記 / #1515）: 「`Mcp:GrpcServices` が構成されているのに gRPC の収集器が
// 無ければ起動時に落とす」を、**本番の Program.cs のまま**ホストが起動しないことで固定する。
//
// `ToolDeclarationSource` のコンストラクタが例外を投げること（`GrpcToolDeclarationCollectorTests` T-G10）と、
// **その例外が実際にホストを止めること**は別の主張である（`ToolPublicationFailFastTests` と同じ区別）。
// 収集器が初めて組まれるのは ToolCatalogRefresher の中であり、そこでの例外は「収集の一時失敗」として握られる ——
// Program.cs が要求を受ける前に 1 度組むことで初めて起動が止まる。待受はしない（TestServer）。
[Trait("TestKind", "Integration")]
public class ToolDeclarationSourceFailFastTests
{
    // gRPC の宛先を構成した McpServer。`removeCollector` で登録の誤り（収集器の登録漏れ）を再現する。
    private sealed class GrpcTargetsFactory(bool removeCollector) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Program.cs はトップレベルで構成を読んで登録を分けるので、読まれる時点に間に合う UseSetting で与える。
            builder.UseSetting("Mcp:GrpcServices:probe", "http://127.0.0.1:1");
            builder.UseSetting("ServiceToken:ClientId", "mcp-server");
            if (removeCollector)
                builder.ConfigureTestServices(services => services.RemoveAll<GrpcToolDeclarationCollector>());
        }
    }

    // 否定形: 収集器の登録が漏れていれば、ホストは起動しない（要求を受ける前に落ちる）。
    [Fact]
    public void Host_does_not_start_when_grpc_targets_are_configured_but_the_collector_is_missing()
    {
        using var factory = new GrpcTargetsFactory(removeCollector: true);

        var act = () => factory.CreateClient().Dispose();

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("Mcp:GrpcServices");
    }

    // 陽性対照: 同じ構成で収集器が登録されていれば起動し、宛先ごとに輸送を選ぶ収集器が組まれる
    // （これが無いと上の試験は「gRPC の宛先があれば必ず落ちる実装」でも緑になる）。
    [Fact]
    public void Host_starts_with_grpc_targets_when_the_collector_is_registered()
    {
        using var factory = new GrpcTargetsFactory(removeCollector: false);

        factory.CreateClient().Dispose();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IToolDeclarationSource>().Should().BeOfType<ToolDeclarationSource>();
        factory.Services.GetService<GrpcToolDeclarationCollector>().Should().NotBeNull();
    }
}
