using AwesomeAssertions;
using System.Net;

namespace Platform.Bff.Tests;

// NFR-19, ADR-0006, [[IADR-0649]] (#2129): BFF から後段への呼び出しで相関 ID が引き継がれる。
// 従前は BFF が受けた X-Correlation-ID を後段へ転送せず、後段は自分で別の GUID を作っていた（突合が 1 ホップで切れる）。
public class BffCorrelationForwardingTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffCorrelationForwardingTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.AuthzManagementStatusCode = HttpStatusCode.OK;
        _factory.AuthzManagementThrows = false;
    }

    [Fact]
    public async Task 受けた相関IDを後段へ引き継ぎ応答にも返す()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "bff-corr-2129");

        var resp = await client.GetAsync("/bff/admin/authz/policies", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastAuthzCorrelationId.Should().Be("bff-corr-2129");
        resp.Headers.GetValues("X-Correlation-ID").Should().ContainSingle().Which.Should().Be("bff-corr-2129");
    }

    [Fact]
    public async Task 相関IDが無ければBFFが決めた値を後段へ送り応答の値と一致する()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/admin/authz/policies", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var returned = resp.Headers.GetValues("X-Correlation-ID").Single();
        _factory.LastAuthzCorrelationId.Should().Be(returned, "後段が受けた値と利用者へ返した値が違うと、問い合わせで突合できない");
    }
}
