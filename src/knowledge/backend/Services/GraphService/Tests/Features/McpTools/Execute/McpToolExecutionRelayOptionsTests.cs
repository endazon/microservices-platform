using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using GraphService.Features.McpTools.Execute;

namespace GraphService.Tests.Features.McpTools.Execute;

// FR-16, NFR-09, 計画 ADR-0117 決定 3, ADR-0086 決定 1, [[IADR-0479]]（2026-09-27 追記 / #1611 段 3）: GraphService の実行口の**信頼する中継者の集合**（`McpToolExecution:`）の
// 構成の束縛と判定（X-48）。既定は `mcp-server` だけ・構成は既定を置き換える・空白だけは誰も信じない・1 つの値は起動時に止める。
//
// 🔴 束縛は本番と同じ登録（`AddMcpToolExecution`）で通す —— .NET の配列の束縛は初期値に構成値を**追記する**ので、
//   「構成したら既定が外れる」は束縛を通さないと観測できない。
[Trait("TestKind", "Unit")]
public class McpToolExecutionRelayOptionsTests
{
    private static McpToolExecutionRelayOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddMcpToolExecution(config);
        return services.BuildServiceProvider().GetRequiredService<IOptions<McpToolExecutionRelayOptions>>().Value;
    }

    private static ClaimsPrincipal ServiceAccount(string clientId) => new(new ClaimsIdentity(
        [new Claim("preferred_username", $"service-account-{clientId}"), new Claim("azp", clientId)],
        "test", "preferred_username", ClaimTypes.Role));

    [Fact]
    public void 未構成なら許可集合はmcp_serverだけ()
    {
        var options = Bind([]);

        options.EffectiveClients.Should().Equal("mcp-server");
        options.TrustsUserContextFrom(ServiceAccount("mcp-server")).Should().BeTrue();
        options.TrustsUserContextFrom(ServiceAccount("aianalysis-service")).Should().BeFalse("他の面の中継者（AI 分析）は実行口の中継者ではない");
        options.TrustsUserContextFrom(ServiceAccount("retrieval-service")).Should().BeFalse("近傍展開（GraphNeighbors:）の中継者は実行口の中継者ではない");
    }

    [Fact]
    public void 構成すると既定を置き換えmcp_serverは残らない()
    {
        var options = Bind(new() { ["McpToolExecution:TrustedUserContextClients:0"] = " other-relay " });

        options.EffectiveClients.Should().Equal("other-relay");
        options.TrustsUserContextFrom(ServiceAccount("mcp-server")).Should().BeFalse("構成は既定を置き換える");
    }

    [Fact]
    public void 空白だけの構成は誰も信じない()
    {
        var options = Bind(new() { ["McpToolExecution:TrustedUserContextClients:0"] = "  " });

        options.EffectiveClients.Should().BeEmpty();
        options.TrustsUserContextFrom(ServiceAccount("mcp-server")).Should().BeFalse();
    }

    // 🔴 1 つの値（配列でない）は束縛されず既定へ静かに戻る。登録（起動）の時点で止める。
    [Fact]
    public void 一つの値で構成すると登録の時点で止まる()
    {
        var act = () => Bind(new() { ["McpToolExecution:TrustedUserContextClients"] = "other-relay,mcp-server" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*McpToolExecution__TrustedUserContextClients__0*");
    }
}
