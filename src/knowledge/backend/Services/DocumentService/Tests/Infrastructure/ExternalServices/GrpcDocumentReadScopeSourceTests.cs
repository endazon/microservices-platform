using AwesomeAssertions;
using DocumentService.Infrastructure.ExternalServices;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Authz;

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

    [Fact]
    public async Task 未構成の縮退は常に読めない()
        => (await new UnavailableDocumentReadScopeSource()
                .ResolveReadBranchesAsync("anyone", TestContext.Current.CancellationToken))
            .Should().BeNull();
}
