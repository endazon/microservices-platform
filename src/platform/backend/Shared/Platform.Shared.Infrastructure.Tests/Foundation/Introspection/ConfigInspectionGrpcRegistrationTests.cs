using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Foundation.Introspection;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Introspection;

// FR-15, NFR-16, ADR-0029, IADR-0379 決定 4, IADR-0462 (#1514), [[IADR-0533]] 決定 3 (#1517):
// 構成情報 API の登録（`AddPlatformConfigInspection`）が gRPC だけの収集器を組み、撤去した旧キーで起動を止めることを固定する。
//
// ［2026-10-10 / #1517］REST の収集は撤去した。宛先は `Introspection:Services`（値は h2c のアドレス）であり、
// gRPC の収集器と s2s トークンの発行側は**常に**登録する（従前は `Introspection:GrpcServices` が在るときだけだった）。
public class ConfigInspectionGrpcRegistrationTests
{
    private static IServiceProvider Build(Dictionary<string, string?> config)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(config);
        builder.AddPlatformConfigInspection();
        return builder.Services.BuildServiceProvider();
    }

    // 収集器は gRPC の収集器の上に組まれ、s2s の発行側が解決できる。
    [Fact]
    public void Registers_the_grpc_collector_and_service_token()
    {
        var sp = Build(new()
        {
            ["Introspection:Services:document-service"] = "http://document-service:8081",
            ["ServiceToken:ClientId"] = "bff",
            ["ServiceToken:ClientSecret"] = "x",
        });

        sp.GetRequiredService<IEffectiveConfigCollector>().Should().BeOfType<EffectiveConfigCollector>();
        sp.GetService<GrpcServiceIntrospectionCollector>().Should().NotBeNull();
        sp.GetService<IServiceTokenProvider>().Should().NotBeNull();
    }

    // 🔴 撤去した旧キー `Introspection:GrpcServices` が残っていれば起動を止める（黙って無視しない）。
    [Theory]
    [InlineData("http://document-service:8081")]
    [InlineData("")]
    public void Retired_grpc_services_key_fails_fast(string value)
    {
        var act = () => Build(new()
        {
            ["Introspection:Services:document-service"] = "http://document-service:8081",
            ["Introspection:GrpcServices:document-service"] = value,
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Introspection:GrpcServices*Introspection:Services*document-service*");
    }

    // 対照: 旧キーが無ければ起動する（宛先が 0 件でも組み立てられる）。
    [Fact]
    public void Without_the_retired_key_it_builds()
    {
        var act = () => Build(new());

        act.Should().NotThrow();
    }
}
