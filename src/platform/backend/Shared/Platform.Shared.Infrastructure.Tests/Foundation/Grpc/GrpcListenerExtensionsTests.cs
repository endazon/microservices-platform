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
//
// ★［2026-09-06 追記］**「どのインタフェースへ bind するか」はここでは分からない。**
// 構成の解決（`ResolveGrpcHost`）を固定するのが本群で、**解決した値が本当に bind へ届いているか**は
// 別の主張である。それは `GrpcListenerBindingTests` がソケットで確かめる ——
// 従前 h2c ポートだけが無条件に全インタフェースへ開いており、本群は緑のままだった。
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

    // NFR-16, ADR-0029, IADR-0379 決定 3: gRPC の待受ホストは **HTTP 側と同じ意図に従う**。
    //
    // 🔴 従前は h2c ポートだけを無条件に全インタフェースへ開いていた。HTTP 側を 127.0.0.1 に
    // 絞っていても gRPC 側は素通しであり、**試験を走らせた端末の外から到達できた**。
    // 本群はその向きを両方向で固定する（狭める側だけを見ると、本番で繋がらない形を作っても気づけない）。

    // コンテナ（`ASPNETCORE_HTTP_PORTS=8080`）はワイルドカード。**全インタフェースのままにする** ——
    // メッシュ内の他 Pod とサイドカーから届く必要がある。
    [Theory]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("0.0.0.0")]
    [InlineData("[::]")]
    public void GrpcHost_stays_wildcard_when_http_binds_all_interfaces(string host)
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config(new()
        {
            [GrpcListenerExtensions.UrlsKey] = $"http://{host}:8080",
        }));

        GrpcListenerExtensions.ResolveGrpcHost(addresses).Should().Be(host);
    }

    // 試験（`ASPNETCORE_URLS=http://127.0.0.1:0`）はループバックのみ。**gRPC も追随する。**
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public void GrpcHost_follows_http_when_http_binds_loopback_only(string host)
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config(new()
        {
            [GrpcListenerExtensions.UrlsKey] = $"http://{host}:0",
        }));

        GrpcListenerExtensions.ResolveGrpcHost(addresses).Should().Be(host);
    }

    // ホストが食い違うときは**広い側へ倒す**。狭めると「本番で繋がらない」に化ける。
    [Fact]
    public void GrpcHost_falls_back_to_wildcard_when_http_hosts_disagree()
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config(new()
        {
            [GrpcListenerExtensions.UrlsKey] = "http://127.0.0.1:5005;http://*:8080",
        }));

        GrpcListenerExtensions.ResolveGrpcHost(addresses).Should().Be("*");
    }

    // 同じホストが複数並ぶだけなら、そのホストに従う（食い違いではない）。
    [Fact]
    public void GrpcHost_follows_the_single_host_even_when_several_ports_share_it()
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config(new()
        {
            [GrpcListenerExtensions.UrlsKey] = "http://127.0.0.1:5005;http://127.0.0.1:5006",
        }));

        GrpcListenerExtensions.ResolveGrpcHost(addresses).Should().Be("127.0.0.1");
    }

    // 陰性対照: 何も構成されていなければ Kestrel 既定（localhost）に従い、全インタフェースへは開かない。
    [Fact]
    public void GrpcHost_follows_the_kestrel_default_which_is_localhost()
    {
        var addresses = GrpcListenerExtensions.ResolveHttpAddresses(Config([]));

        GrpcListenerExtensions.ResolveGrpcHost(addresses).Should().Be("localhost");
    }
}
