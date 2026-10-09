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

// C-86（AC3）: secret の値は、応答以外（アプリケーションのログ・監査ログ）のどこにも出ない（決定 2 の 2）。
[Trait("TestKind", "Integration")]
public class McpClientSecretLeakTests(AuditCapturingFactory factory) : IClassFixture<AuditCapturingFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 登録と再発行のsecretはログにも監査にも出ない()
    {
        var client = factory.Registrar();
        var created = await client.PostAsJsonAsync("/mcp-clients", McpClientTestRequests.ServiceAccount("leak-sa"), Ct);
        var issued = (await created.Content.ReadFromJsonAsync<McpClientRegistrationView>(Ct))!.ClientSecret!;
        var reissued = (await (await client.PostAsync("/mcp-clients/leak-sa/reissue-secret", null, Ct))
            .Content.ReadFromJsonAsync<McpClientSecretView>(Ct))!.ClientSecret;
        // 失敗の経路（502）も通す（例外・Problem の文言に値を入れない）。
        factory.Idp().FailSecretReads = true;
        try
        {
            await client.PostAsJsonAsync("/mcp-clients", McpClientTestRequests.ServiceAccount("leak-fail"), Ct);
        }
        finally
        {
            factory.Idp().FailSecretReads = false;
        }

        // 陽性対照: 記録そのものは取れている（器が何も捕まえていない状態で緑にしない）。
        factory.Logs.Audits.Should().Contain(a => a.Values["AuditAction"] == McpClientAudit.SecretReissueAction);
        factory.Logs.Entries.Should().HaveCountGreaterThan(factory.Logs.Audits.Count, "アプリケーションのログも捕まえている");

        foreach (var secret in new[] { issued, reissued })
        {
            secret.Should().NotBeNullOrEmpty();
            factory.Logs.Entries.Should().NotContain(e => e.Message.Contains(secret), "整形済みの本文に値を出さない");
            factory.Logs.Entries.Should().NotContain(e => e.Values.Values.Any(v => v != null && v.Contains(secret)),
                "構造化の値に値を出さない");
            factory.Logs.Entries.Should().NotContain(e => e.Exception != null && e.Exception.Contains(secret),
                "例外に値を出さない");
        }
    }
}
