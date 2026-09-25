using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Foundation.Introspection;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Introspection;

// FR-15, NFR-16, ADR-0029, IADR-0379 決定 4・5, IADR-0462 (#1514): 構成情報 API の登録（`AddPlatformConfigInspection`）が
// **宛先ごとに輸送を選ぶ収集器**を組み、gRPC の収集器と s2s トークンの発行側を**構成が在るときだけ**入れることを固定する。
public class ConfigInspectionGrpcRegistrationTests
{
    private static IServiceProvider Build(Dictionary<string, string?> config)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(config);
        builder.AddPlatformConfigInspection();
        return builder.Services.BuildServiceProvider();
    }

    // gRPC の宛先があれば、収集器は宛先ごとに輸送を選ぶ実装であり、s2s の発行側と gRPC の収集器が解決できる。
    [Fact]
    public void Grpc_targets_register_the_grpc_collector_and_service_token()
    {
        var sp = Build(new()
        {
            ["Introspection:Services:document-service"] = "http://document-service:8080",
            ["Introspection:GrpcServices:document-service"] = "http://document-service:8081",
            ["ServiceToken:ClientId"] = "bff",
            ["ServiceToken:ClientSecret"] = "x",
        });

        sp.GetRequiredService<IEffectiveConfigCollector>().Should().BeOfType<EffectiveConfigCollector>();
        sp.GetService<GrpcServiceIntrospectionCollector>().Should().NotBeNull();
        sp.GetService<IServiceTokenProvider>().Should().NotBeNull();
    }

    // 対照: gRPC の宛先が無い（あるいは値が空の）配備は資格情報を要求しない —— 既存配備は 1 バイトも変わらない。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_grpc_targets_no_service_token_is_required(string? grpcAddress)
    {
        var config = new Dictionary<string, string?>
        {
            ["Introspection:Services:document-service"] = "http://document-service:8080",
        };
        if (grpcAddress is not null)
            config["Introspection:GrpcServices:document-service"] = grpcAddress;

        var sp = Build(config);

        sp.GetRequiredService<IEffectiveConfigCollector>().Should().BeOfType<EffectiveConfigCollector>();
        sp.GetService<GrpcServiceIntrospectionCollector>().Should().BeNull();
        sp.GetService<IServiceTokenProvider>().Should().BeNull();
    }
}
