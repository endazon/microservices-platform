using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;
using AuthorizationService.Tests.Grpc;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Grpc.Authz.V1;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace AuthorizationService.Tests.Features.Authz.OwnerReadPolicyGuard;

// FR-05, FR-19, NFR-09, ADR-0029, ADR-0075, 計画 ADR-0121 決定 2, [[IADR-0379]], [[IADR-0481]] (#1665):
// 内容の ABAC の門が問う口（`AuthzScope/GetOwnerReadPolicyStatus`）を**実 Kestrel の h2c ポート**で往復する。
//
// 🔴 器（DB）は gRPC の他の試験クラスと共有する（`GrpcServerCollection`）。足したポリシーは必ず消して返す
//   （他のクラスは「ポリシーが 1 件も無ければ seed する」ので、残すと相手の前提が崩れる）。
[Collection(GrpcServerCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcOwnerReadPolicyStatusTests
{
    private const string ServiceSubject = "service-account-document-service";
    private readonly GrpcKestrelFactory _factory;

    public GrpcOwnerReadPolicyStatusTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private AuthzScope.AuthzScopeClient Client() => new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private async Task<int> AskAsync()
    {
        var token = GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);
        var resp = await Client().GetOwnerReadPolicyStatusAsync(
            new GetOwnerReadPolicyStatusRequest(), headers: Bearer(token),
            cancellationToken: TestContext.Current.CancellationToken);
        return resp.ActiveCount;
    }

    private async Task WithPoliciesAsync(AbacPolicy[] policies, Func<Task> body)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
            db.Policies.AddRange(policies);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        try
        {
            await body();
        }
        finally
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
            var ids = policies.Select(p => p.Id).ToHashSet();
            db.Policies.RemoveRange(db.Policies.Where(p => ids.Contains(p.Id)));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }

    private static AbacPolicy Owner(bool active = true, string value = "${current_user}")
    {
        var p = AbacPolicy.Create($"owner-{Guid.NewGuid():N}", PolicyAction.Read, [],
            new Dictionary<string, List<string>> { ["owner"] = [value] });
        if (!active) p.SetActive(false);
        return p;
    }

    // T-46: 陽性対照と否定の対。在れば件数、無効・形違いは数えない。**呼ばれるたびに DB から数える。**
    [Fact]
    public async Task 有効で形の合うポリシーだけを数えて返す()
    {
        var baseline = await AskAsync();

        await WithPoliciesAsync([Owner()], async () => (await AskAsync()).Should().Be(baseline + 1));
        await WithPoliciesAsync([Owner(active: false), Owner(value: "alice")],
            async () => (await AskAsync()).Should().Be(baseline, "無効・形違いは数えない"));
        (await AskAsync()).Should().Be(baseline, "消したら、常駐の直近の値ではなく今の件数を返す");
    }

    // T-47: 陰性対照。資格情報が無ければ UNAUTHENTICATED、利用者のトークンは PERMISSION_DENIED（s2s の面だけ）。
    [Fact]
    public async Task 呼び出し側サービスの資格情報が無ければ拒否する()
    {
        var anonymous = async () => await Client().GetOwnerReadPolicyStatusAsync(
            new GetOwnerReadPolicyStatusRequest(), cancellationToken: TestContext.Current.CancellationToken);
        (await anonymous.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);

        var userToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);
        var forwarded = async () => await Client().GetOwnerReadPolicyStatusAsync(
            new GetOwnerReadPolicyStatusRequest(), headers: Bearer(userToken),
            cancellationToken: TestContext.Current.CancellationToken);
        (await forwarded.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }
}
