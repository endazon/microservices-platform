using AwesomeAssertions;
using McpServer.Domain.Ports;
using McpServer.Features.McpClients;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpServer.Tests.Features.McpClients;

// FR-16, SC-12, 計画 ADR-0123 決定 2・3, [[IADR-0515]] 決定 4 (#1786): **検証 → IdP → 登録簿** と補償。
// 器（WebApplicationFactory）なしで、順序と取り消しだけを固定する。
[Trait("TestKind", "Unit")]
public class IdpFirstWriteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingProvisioner : IServiceAccountProvisioner
    {
        public List<IdpWrite> Undone { get; } = [];
        public bool UndoThrows { get; init; }

        public Task<IdpWrite> CreateAsync(string clientId, string displayName,
            IReadOnlyDictionary<string, string> attributes, CancellationToken ct) => throw new NotSupportedException();

        public Task<IdpWrite> ReplaceAttributesAsync(string clientId, string displayName,
            IReadOnlyDictionary<string, string> attributes, CancellationToken ct) => throw new NotSupportedException();

        public Task UndoAsync(IdpWrite write, CancellationToken ct)
        {
            Undone.Add(write);
            return UndoThrows ? throw new HttpRequestException("down") : Task.CompletedTask;
        }
    }

    private static Task<IResult> Run(
        Func<CancellationToken, Task<IdpWrite>> idp, Func<CancellationToken, Task<IResult>> registry,
        IServiceAccountProvisioner provisioner)
        => IdpFirstWrite.RunAsync(idp, registry, provisioner, NullLogger.Instance, Ct);

    // T-1786-21: 登録簿への書き込みが失敗したら、IdP に書いたもの（作ったクライアント）を取り消す。
    [Fact]
    public async Task 登録簿への書き込みが失敗したらIdPの書き込みを取り消す()
    {
        var provisioner = new RecordingProvisioner();
        var written = new IdpWrite(IdpWriteKind.Created, "agent-a", "c1", "u1");

        var act = () => Run(_ => Task.FromResult(written),
            _ => throw new InvalidOperationException("db down"), provisioner);

        await act.Should().ThrowAsync<InvalidOperationException>("元の失敗を投げる");
        provisioner.Undone.Should().ContainSingle().Which.Should().Be(written);
    }

    // T-1786-22: 差し替えの取り消しは、書く前の属性を持った書き込みを口へ返す。
    [Fact]
    public async Task 差し替えの取り消しは書く前の属性を持って口へ返る()
    {
        var provisioner = new RecordingProvisioner();
        var previous = new Dictionary<string, string> { ["clearance"] = "public" };
        var written = new IdpWrite(IdpWriteKind.Updated, "agent-a", "c1", "u1", previous);

        var act = () => Run(_ => Task.FromResult(written),
            _ => throw new InvalidOperationException("db down"), provisioner);

        await act.Should().ThrowAsync<InvalidOperationException>();
        provisioner.Undone.Single().PreviousAttributes.Should().BeEquivalentTo(previous);
    }

    // T-1786-23: 取り消しも失敗したら、補償の失敗ではなく元の失敗を投げる（理由を上書きしない）。
    [Fact]
    public async Task 取り消しが失敗しても元の失敗を投げる()
    {
        var provisioner = new RecordingProvisioner { UndoThrows = true };

        var act = () => Run(_ => Task.FromResult(new IdpWrite(IdpWriteKind.Created, "agent-a", "c1", "u1")),
            _ => throw new InvalidOperationException("db down"), provisioner);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("db down");
    }

    // T-1786-24（否定形）: IdP へ書けなければ登録簿へ書かない（Failed は 502・Unavailable は 503）。
    [Theory]
    [InlineData(IdpProvisioningFailure.Failed, StatusCodes.Status502BadGateway)]
    [InlineData(IdpProvisioningFailure.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    public async Task IdPへ書けなければ登録簿へ書かない(IdpProvisioningFailure failure, int status)
    {
        var provisioner = new RecordingProvisioner();
        var registryCalled = false;

        var result = await Run(_ => throw new IdpProvisioningException(failure, "x"),
            _ => { registryCalled = true; return Task.FromResult(Results.Ok()); }, provisioner);

        registryCalled.Should().BeFalse();
        result.Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(status);
        provisioner.Undone.Should().BeEmpty("何も書いていないので取り消すものも無い");
    }

    // T-1786-25（否定形）: IdP に既にあるクライアントなら、登録簿へ書かずに 400 で拒む（取り消しもしない）。
    [Fact]
    public async Task IdPに既にあれば登録簿へ書かずに拒む()
    {
        var provisioner = new RecordingProvisioner();
        var registryCalled = false;

        var result = await Run(_ => Task.FromResult(IdpWrite.AlreadyExisting("identity-admin")),
            _ => { registryCalled = true; return Task.FromResult(Results.Ok()); }, provisioner);

        registryCalled.Should().BeFalse();
        result.Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        provisioner.Undone.Should().BeEmpty("入口が作っていないものを消さない");
    }
}
