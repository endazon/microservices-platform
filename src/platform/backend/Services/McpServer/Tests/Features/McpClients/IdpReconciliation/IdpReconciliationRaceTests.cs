using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Features.McpClients;
using McpServer.Features.McpClients.IdpReconciliation;
using McpServer.Infrastructure.ExternalServices;
using McpServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpServer.Tests.Features.McpClients.IdpReconciliation;

// FR-16, SC-12, 計画 ADR-0123 決定 2, [[IADR-0516]] 決定 4 の残余・決定 5（2026-10-09 追記 / #1818）:
// **交差した差し替えの後勝ち（IdP は後の要求・登録簿は先の要求）は、照合が検知する**（issue #1818 の受け入れ基準 6）。
// 行の排他は入れていない（IADR-0516 決定 4）ので、2 つの差し替えが「IdP へ A → IdP へ B → 登録簿へ B → 登録簿へ A」の順に
// 交差すると、どちらの要求も成功したまま食い違いが残る。本試験は本物の `IdpFirstWrite` とプロセス内の IdP でその順を作る。
[Trait("TestKind", "Unit")]
public class IdpReconciliationRaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static McpDbContext Db(string name)
        => new(new DbContextOptionsBuilder<McpDbContext>().UseInMemoryDatabase(name).Options);

    private static Dictionary<string, string> Attrs(string clearance)
        => new(StringComparer.Ordinal) { ["clearance"] = clearance };

    // 1 つの差し替えを IdpFirstWrite に通す。登録簿への書き込みは `registryGate` が開くまで待つ（交差を作るため）。
    private static Task<IResult> Replace(
        string dbName, InMemoryServiceAccountProvisioner idp, string clientId, Dictionary<string, string> attributes,
        Task registryGate)
        => IdpFirstWrite.RunAsync(
            _ => idp.ReplaceAttributesAsync(clientId, clientId, attributes, enabled: true, CancellationToken.None),
            async ct =>
            {
                await registryGate;
                using var db = Db(dbName);
                var row = await db.Clients.SingleAsync(c => c.ClientId == clientId, ct);
                row.ReplaceAttributes(attributes, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok();
            },
            idp, NullLogger.Instance, Ct);

    // C-47（受け入れ基準 6）: 交差した差し替えは両方とも成功し、IdP は後の要求・登録簿は先の要求の値になる。照合はそれを
    // 「属性が違う」として検知し、ゲージが 1・結末が drift になる。照合は直さない（IdP は後の要求の値のまま）。
    [Fact]
    public async Task 交差した差し替えの後勝ちは照合が属性の食い違いとして検知する()
    {
        var dbName = $"IdpReconciliationRace_{Guid.NewGuid()}";
        var idp = new InMemoryServiceAccountProvisioner();
        await idp.CreateAsync("agent-r", "agent-r", Attrs("public"), Ct);
        using (var seed = Db(dbName))
        {
            seed.Clients.Add(McpClient.Register("agent-r", "agent-r", McpClientKind.ServiceAccount, Attrs("public"),
                EgressTier.StandardExternal, DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync(Ct);
        }

        // 要求 A: IdP へ書き、登録簿の手前で止まる。
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestA = Replace(dbName, idp, "agent-r", Attrs("internal"), releaseA.Task);
        // 要求 B: A の後に IdP へ書き、登録簿まで書き終える。
        var resultB = await Replace(dbName, idp, "agent-r", Attrs("confidential"), Task.CompletedTask);
        // A が登録簿へ書く（後勝ち）。
        releaseA.SetResult();
        var resultA = await requestA;

        ((IStatusCodeHttpResult)resultA).StatusCode.Should().Be(StatusCodes.Status200OK, "どちらの要求も成功に見える");
        ((IStatusCodeHttpResult)resultB).StatusCode.Should().Be(StatusCodes.Status200OK);
        (await ((IServiceAccountDirectory)idp).ReadServiceAccountAttributesAsync("agent-r", Ct))!["clearance"]
            .Should().Be("confidential", "IdP は後の要求（B）の値");
        using (var read = Db(dbName))
            (await read.Clients.SingleAsync(Ct)).Attributes["clearance"].Should().Be("internal", "登録簿は先の要求（A）の値");

        using var db = Db(dbName);
        var (check, _, probe, _) = IdpReconciliationTests.Arrange(db, idp);
        using var _p = probe;

        var drifts = await check.RunAsync(Ct);

        drifts.Should().ContainSingle().Which.Should().Be(new IdpDrift("agent-r", IdpDriftKind.AttributesDiffer));
        probe.CollectGauge().Should().Equal(1);
        probe.Outcome(IdpReconciliationMetrics.OutcomeDrift).Should().Be(1);
        (await ((IServiceAccountDirectory)idp).ReadServiceAccountAttributesAsync("agent-r", Ct))!["clearance"]
            .Should().Be("confidential", "照合は直さない");
    }
}
