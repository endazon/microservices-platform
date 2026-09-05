using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Grpc;

// NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 3 (#1201): h2c リスナの構成読み取りを固定する。
//
// 🔴 要点は「gRPC を有効にした瞬間に HTTP/1.1 のポートが消えない」こと。Kestrel は Listen* を 1 つでも
// 構成するとホスティング URL を捨てるため、共通ヘルパはホスティング構成（urls → http_ports → 既定）から
// HTTP 側のアドレスを読み直して再宣言する。ここではその読み取りを固定し、実 bind は
// AuthorizationService.Tests の GrpcResolveScopeTests（T-02 / T-07）が観測する。
public class GrpcListenerExtensionsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    public void GrpcPort_unset_or_zero_means_no_listener(string? raw)
    {
        var port = GrpcListenerExtensions.ResolveGrpcPort(
            Config(new() { [GrpcListenerExtensions.PortKey] = raw }));

        port.Should().BeNull();
    }

    [Fact]
    public void GrpcPort_is_read_when_set()
    {
        GrpcListenerExtensions.ResolveGrpcPort(Config(new() { [GrpcListenerExtensions.PortKey] = "8081" }))
            .Should().Be(8081);
    }

    // 構成の綴り誤り・範囲外は黙って「立てない」へ倒さない（「gRPC が来ない」が設定誤りと区別できなくなる）。
    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("70000")]
    public void GrpcPort_invalid_value_throws(string raw)
    {
        var act = () => GrpcListenerExtensions.ResolveGrpcPort(
            Config(new() { [GrpcListenerExtensions.PortKey] = raw }));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{GrpcListenerExtensions.PortKey}*");
    }

    // ASPNETCORE_URLS（`urls`）が最優先。複数は `;` 区切り。
    [Fact]
    public void HttpAddresses_come_from_urls_first()
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config(new()
        {
            [GrpcListenerExtensions.UrlsKey] = "http://+:8080;http://127.0.0.1:5005",
            [GrpcListenerExtensions.HttpPortsKey] = "9999",
        }));

        addresses.Select(a => (a.Host, a.Port)).Should().Equal(("+", 8080), ("127.0.0.1", 5005));
    }

    // ASPNETCORE_HTTP_PORTS（`http_ports`。.NET 8 以降のコンテナ既定）は urls が無いときに効く。
    [Fact]
    public void HttpAddresses_fall_back_to_http_ports()
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config(new()
        {
            [GrpcListenerExtensions.HttpPortsKey] = "8080;8090",
        }));

        addresses.Select(a => (a.Scheme, a.Host, a.Port)).Should().Equal(("http", "*", 8080), ("http", "*", 8090));
    }

    // どちらも無ければ Kestrel の既定（localhost:5000）を再現する（ホスティングが 1 つも bind しない形にしない）。
    [Fact]
    public void HttpAddresses_default_to_kestrel_default_when_nothing_is_configured()
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config([]));

        addresses.Should().ContainSingle().Which.ToString().Should().Be(GrpcListenerExtensions.DefaultHttpUrl);
    }
}
