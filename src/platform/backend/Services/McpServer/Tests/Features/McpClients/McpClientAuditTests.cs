using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Features.McpClients;
using McpServer.Infrastructure.ExternalServices;
using McpServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServer.Tests.Features.McpClients;

// C-82〜C-85（AC2 / FU5）: SC-12 の管理操作を既存の監査ログ（`AuditLogger`）に残す。値は残さない。
[Trait("TestKind", "Integration")]
public class McpClientAuditTests(AuditCapturingFactory factory) : IClassFixture<AuditCapturingFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IEnumerable<CapturingLoggerProvider.Entry> AuditsFor(string clientId)
        => factory.Logs.Audits.Where(a => (a.Values.GetValueOrDefault("AuditDetail") ?? "").Contains($"client={clientId}"));

    private static (string? Action, string? Subject, string? Outcome, string? Detail) Parts(CapturingLoggerProvider.Entry e)
        => (e.Values.GetValueOrDefault("AuditAction"), e.Values.GetValueOrDefault("AuditSubject"),
            e.Values.GetValueOrDefault("AuditOutcome"), e.Values.GetValueOrDefault("AuditDetail"));

    // C-82: 無人の登録は「登録」と「secret の発行」の 2 行を、誰が（利用者名）・どのクライアントで残す。
    [Fact]
    public async Task 無人の登録は登録とsecretの発行を監査に残す()
    {
        await factory.Registrar().PostAsJsonAsync("/mcp-clients", McpClientTestRequests.ServiceAccount("aud-sa"), Ct);

        var rows = AuditsFor("aud-sa").Select(Parts).ToList();
        rows.Should().ContainSingle(r => r.Action == McpClientAudit.RegisterAction && r.Outcome == "granted"
            && r.Subject == McpClientTestRequests.AdminName && r.Detail!.Contains("kind=service-account")
            && r.Detail.Contains("attributes=clearance=public") && r.Detail.Contains("status=201"));
        rows.Should().ContainSingle(r => r.Action == McpClientAudit.SecretIssueAction && r.Outcome == "granted"
            && r.Subject == McpClientTestRequests.AdminName);
    }

    // C-83: 有人の登録は発行の行を残さない（secret が無い）。拒否（部分集合の外れ）も denied で残す。
    [Fact]
    public async Task 有人の登録は発行を残さず拒否もdeniedで残す()
    {
        await factory.Registrar().PostAsJsonAsync("/mcp-clients", McpClientTestRequests.Interactive("aud-human"), Ct);
        await factory.Registrar().PostAsJsonAsync("/mcp-clients", new RegisterMcpClientRequest("aud-over", "x", "service-account",
            new Dictionary<string, string> { ["clearance"] = "restricted" }), Ct);

        AuditsFor("aud-human").Select(Parts).Should().ContainSingle(r => r.Action == McpClientAudit.RegisterAction && r.Outcome == "granted")
            .And.NotContain(r => r.Action == McpClientAudit.SecretIssueAction);
        AuditsFor("aud-over").Select(Parts).Should().ContainSingle(r => r.Action == McpClientAudit.RegisterAction
            && r.Outcome == "denied" && r.Detail!.Contains("status=400"))
            .And.NotContain(r => r.Action == McpClientAudit.SecretIssueAction);
    }

    // C-84: 差し替え・無効化・再有効化・再発行をそれぞれの action で残す。不在は not-found。
    [Fact]
    public async Task 差し替え無効化再有効化再発行を監査に残す()
    {
        var client = factory.Registrar();
        await client.PostAsJsonAsync("/mcp-clients", McpClientTestRequests.ServiceAccount("aud-ops"), Ct);
        await client.PutAsJsonAsync("/mcp-clients/aud-ops/attributes",
            new ReplaceMcpClientAttributesRequest(new Dictionary<string, string> { ["clearance"] = "internal" }), Ct);
        await client.PostAsync("/mcp-clients/aud-ops/disable", null, Ct);
        await client.PostAsync("/mcp-clients/aud-ops/enable", null, Ct);
        await client.PostAsync("/mcp-clients/aud-ops/reissue-secret", null, Ct);
        await client.PostAsync("/mcp-clients/aud-missing/disable", null, Ct);

        var rows = AuditsFor("aud-ops").Select(Parts).ToList();
        rows.Should().ContainSingle(r => r.Action == McpClientAudit.ReplaceAttributesAction && r.Outcome == "granted"
            && r.Detail!.Contains("attributes=clearance=internal"));
        rows.Should().ContainSingle(r => r.Action == McpClientAudit.DisableAction && r.Outcome == "granted");
        rows.Should().ContainSingle(r => r.Action == McpClientAudit.EnableAction && r.Outcome == "granted");
        rows.Should().ContainSingle(r => r.Action == McpClientAudit.SecretReissueAction && r.Outcome == "granted"
            && r.Subject == McpClientTestRequests.AdminName);
        AuditsFor("aud-missing").Select(Parts).Should().ContainSingle(r => r.Action == McpClientAudit.DisableAction
            && r.Outcome == "not-found");
    }

    // C-85: 有人の再発行（拒否）も残す。
    [Fact]
    public async Task 再発行の拒否もdeniedで残す()
    {
        await factory.Registrar().PostAsJsonAsync("/mcp-clients", McpClientTestRequests.Interactive("aud-re-human"), Ct);
        await factory.Registrar().PostAsync("/mcp-clients/aud-re-human/reissue-secret", null, Ct);

        AuditsFor("aud-re-human").Select(Parts).Should().ContainSingle(r => r.Action == McpClientAudit.SecretReissueAction
            && r.Outcome == "denied");
    }
}
