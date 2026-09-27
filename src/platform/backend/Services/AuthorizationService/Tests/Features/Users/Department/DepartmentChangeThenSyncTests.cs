using AuthorizationService.Domain;
using AuthorizationService.Features.Authz;
using AuthorizationService.Features.Users.Department;
using AuthorizationService.Features.Users.DepartmentSync;
using AuthorizationService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.Metrics;

namespace AuthorizationService.Tests.Features.Users.Department;

// FR-05, FR-09, UC-05, SC-17, 計画 ADR-0116 決定 1, ADR-0115 決定 3, [[IADR-0473]] (#1610):
// T-63 🔴 **SC-17 で部門を変えた後に部門の同期（`Fix`）を 1 周させても、部門は戻らない。**
//
// 従前（#1573）は SC-17 が属性を直接書き、同期が次の周期で属性をグループの値へ戻していた。いまは SC-17 がグループを変え、
// 同期が属性をその新しいグループへ追随させる。SC-17 の変更と同期を同じ偽物の realm（`InMemoryIdentityAdminClient`）の上で続けて回す。
[Trait("TestKind", "Unit")]
public class DepartmentChangeThenSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IMeterFactory Meters =
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();

    [Fact]
    public async Task One_sync_cycle_after_the_change_follows_the_new_group_instead_of_reverting_it()
    {
        var realm = new InMemoryIdentityAdminClient(NullLogger<InMemoryIdentityAdminClient>.Instance);
        var service = new UserDepartmentService(
            realm, new AttributeDictionary(realm, NullLogger<AttributeDictionary>.Instance),
            NullLogger<UserDepartmentService>.Instance);
        var sync = new DepartmentAttributeSync(
            realm, new DepartmentAttributeSyncMetrics(Meters), NullLogger<DepartmentAttributeSync>.Instance);

        // 前提: 佐藤は engineering の部門グループに 1 つ属し、属性も engineering（同期の下で一致している）。
        (await sync.RunAsync(DepartmentAttributeSyncMode.Fix, Ct)).Findings
            .Single(f => f.UserId == "u-sato").Verdict.Should().Be(DepartmentAttributeVerdict.InSync);

        var changed = await service.ChangeAsync("u-sato", "sales", Ct);
        changed.Kind.Should().Be(UserDepartmentOutcomeKind.Ok);
        (await realm.FindByIdAsync("u-sato", Ct))!.Attributes["department"]
            .Should().Be("engineering", "SC-17 は属性を書かない（同期が追いつく）");

        var cycle = await sync.RunAsync(DepartmentAttributeSyncMode.Fix, Ct);

        (await realm.FindByIdAsync("u-sato", Ct))!.Attributes["department"]
            .Should().Be("sales", "同期は属性を新しい部門グループへ追随させる（元の部門へ戻さない）");
        (await realm.GetUserGroupsAsync("u-sato", Ct)).Select(g => g.Path)
            .Should().Contain("/department/sales").And.NotContain("/department/engineering", "同期はグループを変えない");
        cycle.Findings.Single(f => f.UserId == "u-sato").Expected.Should().Be("sales");

        // 2 周目: 何も変わらない（部門は sales のまま）。
        var second = await sync.RunAsync(DepartmentAttributeSyncMode.Fix, Ct);
        second.Findings.Single(f => f.UserId == "u-sato").Verdict.Should().Be(DepartmentAttributeVerdict.InSync);
        (await realm.FindByIdAsync("u-sato", Ct))!.Attributes["department"].Should().Be("sales");
    }

    // T-63: 「部門なし」にした後の同期は、属性を消す（ADR-0116 決定 2。部門グループ 0 個の人は部門を持たない）。戻さない。
    [Fact]
    public async Task One_sync_cycle_after_choosing_no_department_clears_the_attribute_instead_of_restoring_it()
    {
        var realm = new InMemoryIdentityAdminClient(NullLogger<InMemoryIdentityAdminClient>.Instance);
        var service = new UserDepartmentService(
            realm, new AttributeDictionary(realm, NullLogger<AttributeDictionary>.Instance),
            NullLogger<UserDepartmentService>.Instance);
        var sync = new DepartmentAttributeSync(
            realm, new DepartmentAttributeSyncMetrics(Meters), NullLogger<DepartmentAttributeSync>.Instance);

        (await service.ChangeAsync("u-sato", null, Ct)).Kind.Should().Be(UserDepartmentOutcomeKind.Ok);
        await sync.RunAsync(DepartmentAttributeSyncMode.Fix, Ct);

        (await realm.FindByIdAsync("u-sato", Ct))!.Attributes.Should().NotContainKey("department");
        (await realm.GetUserGroupsAsync("u-sato", Ct)).Select(g => g.Path).Should().NotContain(
            p => p.StartsWith("/department/", StringComparison.Ordinal), "同期は部門グループへ入れ直さない");
    }
}
