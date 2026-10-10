using AwesomeAssertions;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// 🔴 FR-16, NFR-16, IADR-0462（2026-09-26 追記 / #1515）, [[IADR-0533]] 決定 3 (#1517):
// 撤去した旧キー `Mcp:GrpcServices` が残っていれば、**本番の Program.cs のまま**ホストが起動しないことを固定する。
//
// ［2026-10-10 / #1517］従前は「`Mcp:GrpcServices` が構成されているのに gRPC の収集器が無ければ落とす」を固定していた。
// REST の収集を撤去し、宛先を `Mcp:Services`（値は gRPC の宛先）へ一本化したので、落とす条件は「旧キーが残っている」へ変わった
// （移し忘れた上書き値を黙って無視すると、ツールが静かに消えるだけになる）。待受はしない（TestServer）。
[Trait("TestKind", "Integration")]
public class ToolDeclarationSourceFailFastTests
{
    private sealed class TargetsFactory(string key) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Program.cs はトップレベルで構成を読んで登録するので、読まれる時点に間に合う UseSetting で与える。
            builder.UseSetting($"{key}:probe", "http://127.0.0.1:1");
            builder.UseSetting("ServiceToken:ClientId", "mcp-server");
        }
    }

    // 否定形: 旧キーが残っていれば、ホストは起動しない（要求を受ける前に落ちる）。
    [Fact]
    public void Host_does_not_start_when_the_retired_grpc_services_key_remains()
    {
        using var factory = new TargetsFactory(GrpcToolDeclarationCollector.RetiredGrpcServicesSection);

        var act = () => factory.CreateClient().Dispose();

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("Mcp:GrpcServices");
    }

    // 陽性対照: 同じ宛先を新しいキー `Mcp:Services` で与えれば起動し、gRPC の収集器と実行器が組まれる
    // （これが無いと上の試験は「宛先があれば必ず落ちる実装」でも緑になる）。
    [Fact]
    public void Host_starts_with_targets_under_the_services_key()
    {
        using var factory = new TargetsFactory(GrpcToolDeclarationCollector.ServicesSection);

        factory.CreateClient().Dispose();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IToolDeclarationSource>().Should().BeOfType<ToolDeclarationSource>();
        factory.Services.GetService<GrpcToolDeclarationCollector>().Should().NotBeNull();
        factory.Services.GetRequiredService<McpServer.Domain.IToolInvoker>().Should().BeOfType<GrpcToolInvoker>();
    }
}
