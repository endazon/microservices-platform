using AuthorizationService.Domain;
using AuthorizationService.Domain.Ports;
using AwesomeAssertions;

namespace AuthorizationService.Tests.Domain;

// FR-05, FR-09, SC-17, 計画 ADR-0116 決定 1, [[IADR-0473]] (#1610): SC-17 の部門欄の保存を所属の変更として計画する純関数。
[Trait("TestKind", "Unit")]
public class DepartmentMembershipPlanTests
{
    private static IdentityGroup G(string id, string path) => new(id, path[(path.LastIndexOf('/') + 1)..], path);

    private static readonly IdentityGroup Eng = G("g-eng", "/department/engineering");
    private static readonly IdentityGroup Backend = G("g-backend", "/department/engineering/backend");
    private static readonly IdentityGroup Sales = G("g-sales", "/department/sales");
    private static readonly IdentityGroup Hr = G("g-hr", "/department/hr");
    private static readonly IdentityGroup Teams = G("g-teams-sales", "/teams/sales");
    private static readonly IdentityGroup Root = G("g-dept", "/department");

    // T-62: 別の部門へ移す ＝ 目的のグループへ入れ、部門の木の直接の所属をすべて外す（部門の木の外・根は触らない）。
    [Fact]
    public void Moving_joins_the_target_and_leaves_every_other_department_group_only()
    {
        var plan = DepartmentMembershipPlan.Plan([Eng, Backend, Teams, Root], "sales", "g-sales");

        plan.Verdict.Should().Be(DepartmentMembershipVerdict.Move);
        plan.CurrentCodes.Should().Equal("engineering");
        plan.JoinGroupId.Should().Be("g-sales");
        plan.LeaveGroupIds.Should().BeEquivalentTo(["g-eng", "g-backend"], "`/teams/sales`（名前が同じ別の木）と根 `/department` は触らない");
    }

    // T-62: 部門なし ＝ 入れず、部門グループをすべて外す。
    [Fact]
    public void No_department_leaves_every_department_group_and_joins_nothing()
    {
        var plan = DepartmentMembershipPlan.Plan([Sales, Teams], null, null);

        plan.Verdict.Should().Be(DepartmentMembershipVerdict.Move);
        plan.JoinGroupId.Should().BeNull();
        plan.LeaveGroupIds.Should().Equal("g-sales");
    }

    // 冪等: すでに目的の状態なら何もしない（入れ子だけに属する人の同じ部門・部門なしで 0 個）。
    [Fact]
    public void The_current_state_is_left_unchanged()
    {
        DepartmentMembershipPlan.Plan([Backend, Teams], "engineering", "g-eng").Verdict
            .Should().Be(DepartmentMembershipVerdict.Unchanged, "入れ子は上位のコードに畳む（同じ部門なら入れ子の所属を崩さない）");
        DepartmentMembershipPlan.Plan([Teams], null, null).Verdict.Should().Be(DepartmentMembershipVerdict.Unchanged);
        DepartmentMembershipPlan.Plan([Eng], "engineering", "g-eng").Verdict.Should().Be(DepartmentMembershipVerdict.Unchanged);
    }

    // 部門グループ 0 個の人を部門へ入れる（外すものは無い）。
    [Fact]
    public void A_user_in_no_department_group_is_joined()
    {
        var plan = DepartmentMembershipPlan.Plan([Teams], "hr", "g-hr");

        plan.Verdict.Should().Be(DepartmentMembershipVerdict.Move);
        plan.JoinGroupId.Should().Be("g-hr");
        plan.LeaveGroupIds.Should().BeEmpty();
    }

    // T-65: 🔴 2 個以上の部門グループ（コードで数える）なら変えない。目的が片方でも、部門なしでも。
    [Theory]
    [InlineData("sales")]
    [InlineData("engineering")]
    [InlineData(null)]
    public void Several_department_codes_are_never_planned(string? target)
    {
        var plan = DepartmentMembershipPlan.Plan([Sales, Hr], target, target is null ? null : "g-" + target);

        plan.Verdict.Should().Be(DepartmentMembershipVerdict.MultipleDepartments);
        plan.CurrentCodes.Should().Equal("hr", "sales");
        plan.JoinGroupId.Should().BeNull();
        plan.LeaveGroupIds.Should().BeEmpty();
    }

    // 同じ部門の入れ子（engineering と engineering/backend）は 1 つの部門である（2 個以上にしない）。
    [Fact]
    public void Nested_groups_of_one_department_count_as_one()
        => DepartmentMembershipPlan.Plan([Eng, Backend], "hr", "g-hr").Verdict.Should().Be(DepartmentMembershipVerdict.Move);

    [Fact]
    public void A_target_code_without_its_group_id_is_a_programming_error()
        => ((Action)(() => DepartmentMembershipPlan.Plan([], "sales", null))).Should().Throw<ArgumentException>();
}
