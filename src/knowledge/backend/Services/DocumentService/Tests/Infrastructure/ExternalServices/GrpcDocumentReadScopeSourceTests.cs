using AwesomeAssertions;
using DocumentService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace DocumentService.Tests.Infrastructure.ExternalServices;

// FR-19, NFR-09, 計画 ADR-0119 決定 3 (#1614): 認可サービスの応答 → 分岐の並び の写像と、未構成の縮退。
// 🔴 「許可なし」と「未構成」は**読めない（null）**へ倒れ、分岐の無い応答は従来の連言 1 つを 1 分岐として読む
// （BFF の `IsReadable` と同じ読み方）。
public class GrpcDocumentReadScopeSourceTests
{
    private static readonly AttributeFilter Shared = new("shared_with", ["bob", "grp-1"]);
    private static readonly AttributeFilter Internal = new("confidentiality", ["internal"]);

    [Fact]
    public void 許可なしの応答は読めない()
        => GrpcDocumentReadScopeSource.ToBranches(new BffAccessScope([Internal], GrantsAccess: false)).Should().BeNull();

    [Fact]
    public void 分岐のある応答は分岐の並びになる()
    {
        var branches = GrpcDocumentReadScopeSource.ToBranches(new BffAccessScope(
            [Internal], GrantsAccess: true,
            [new AccessScopeBranch("共有", [Shared]), new AccessScopeBranch("組織", [Internal])]));

        branches.Should().NotBeNull();
        branches!.Should().HaveCount(2);
        branches[0].Should().Equal(Shared);
        branches[1].Should().Equal(Internal);
    }

    [Fact]
    public void 分岐の無い応答は従来の連言を1分岐として読む()
    {
        var branches = GrpcDocumentReadScopeSource.ToBranches(new BffAccessScope([Internal, Shared], GrantsAccess: true));

        branches.Should().ContainSingle().Which.Should().Equal(Internal, Shared);
    }

    // ［2026-09-27 / #1646 監査］認可サービスが応答しなければ、自分の上限で打ち切って**読めない（null）**へ倒す ——
    // 要求の ct は立っていないので取り消しにはしない。Grpc.Net.Client の既定（取り消しを `RpcException(Cancelled)` で投げる）と、
    // OCE で終わる形の両方を見る（共有クライアントはどちらも、渡された上限つきの token の OCE として外へ出す）。
    [Theory(Timeout = 10_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 認可サービスが応答しなければ上限で打ち切って読めない(bool asRpcException)
    {
        var source = new GrpcDocumentReadScopeSource(
            new AuthzScopeGrpcClient(new HangingAuthzScopeClient(asRpcException), NullLogger<AuthzScopeGrpcClient>.Instance),
            TimeSpan.FromMilliseconds(50));

        (await source.ResolveReadBranchesAsync("alice", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    // 対照: 要求そのものが取り消されたら、読めない（null）へ畳まず取り消しとして伝える。
    [Theory(Timeout = 10_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 要求そのものが取り消されたら取り消しをそのまま伝える(bool asRpcException)
    {
        var source = new GrpcDocumentReadScopeSource(
            new AuthzScopeGrpcClient(new HangingAuthzScopeClient(asRpcException), NullLogger<AuthzScopeGrpcClient>.Instance),
            TimeSpan.FromMinutes(1));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = () => source.ResolveReadBranchesAsync("alice", cts.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken.Should().Be(cts.Token);
    }

    // 取り消されるまで応答しない `AuthzScope/Resolve`。
    private sealed class HangingAuthzScopeClient(bool asRpcException) : Pb.AuthzScope.AuthzScopeClient
    {
        public override AsyncUnaryCall<Pb.ResolveScopeResponse> ResolveAsync(Pb.ResolveScopeRequest request, CallOptions options)
            => new(HangAsync(options.CancellationToken), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });

        private async Task<Pb.ResolveScopeResponse> HangAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) when (asRpcException)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, "fake"));
            }
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task 未構成の縮退は常に読めない()
        => (await new UnavailableDocumentReadScopeSource()
                .ResolveReadBranchesAsync("anyone", TestContext.Current.CancellationToken))
            .Should().BeNull();
}
