using AuthorizationService.Domain;
using AwesomeAssertions;

namespace AuthorizationService.Tests.Domain;

// FR-05, FR-09, UC-05, SC-17, 計画 ADR-0115 決定 3, [[IADR-0473]] (#1573): 属性を部門グループへ合わせる計画（純関数）。
//
// 受け入れ基準の写像（#1573）: T-D-01 食い違いの検知 / T-D-02 グループ側へ直す / T-D-03 0 個・複数は上書きしない /
// T-D-04 冪等（直した後の再計画は食い違い 0）。
[Trait("TestKind", "Unit")]
public class DepartmentAttributeReconciliationTests
{
    private static IReadOnlySet<string> Codes(params string[] codes) => new HashSet<string>(codes, StringComparer.Ordinal);

    [Theory]
    [InlineData("/department/engineering", "engineering")]
    [InlineData("/department/engineering/backend", "engineering")] // 入れ子は上位に畳む
    [InlineData("/department", null)]                              // 親そのもの
    [InlineData("/department/", null)]                             // 直下が空
    [InlineData("/teams/sales", null)]                             // 別の木（名前が同じでも部門ではない）
    [InlineData("/Department/sales", null)]                        // 大小文字違いの根
    [InlineData(null, null)]
    public void CodeOf_takes_the_first_segment_under_department(string? path, string? expected)
        => DepartmentAttributeReconciliation.CodeOf(path).Should().Be(expected);

    // T-D-01 / T-D-02: 属性が違う・属性が無い → Mismatch で、直す先は**グループのコード**（属性ではない）。
    [Fact]
    public void Mismatch_is_detected_and_expected_value_is_the_group_code()
    {
        var findings = DepartmentAttributeReconciliation.Plan(
            new Dictionary<string, IReadOnlySet<string>>
            {
                ["u-wrong"] = Codes("sales"),
                ["u-missing"] = Codes("hr"),
                ["u-case"] = Codes("sales"),
                ["u-ok"] = Codes("engineering"),
            },
            new Dictionary<string, string?>
            {
                ["u-wrong"] = "engineering",
                ["u-missing"] = null,
                ["u-case"] = "Sales",
                ["u-ok"] = "engineering",
            });

        findings.Single(f => f.UserId == "u-wrong").Should().BeEquivalentTo(new
        {
            Verdict = DepartmentAttributeVerdict.Mismatch,
            Current = "engineering",
            Expected = "sales",
        });
        findings.Single(f => f.UserId == "u-missing").Expected.Should().Be("hr");
        findings.Single(f => f.UserId == "u-case").Verdict.Should().Be(DepartmentAttributeVerdict.Mismatch,
            "照合は序数（大小文字違いは食い違い）");
        findings.Single(f => f.UserId == "u-ok").Verdict.Should().Be(DepartmentAttributeVerdict.InSync);
    }

    // T-D-03: 🔴 0 個・2 個以上は Unresolved で、直す先を持たない（Expected = null）。
    // 「先頭を採る」「属性に一致する方を採る」へ変える変異はここで赤になる。
    [Fact]
    public void Zero_or_several_department_groups_are_unresolved_and_have_no_target()
    {
        var findings = DepartmentAttributeReconciliation.Plan(
            new Dictionary<string, IReadOnlySet<string>>
            {
                ["u-two"] = Codes("sales", "hr"),
                ["u-none"] = Codes(),
            },
            new Dictionary<string, string?> { ["u-two"] = "hr", ["u-none"] = "sales" });

        findings.Should().OnlyContain(f => f.Verdict == DepartmentAttributeVerdict.Unresolved && f.Expected == null);
        findings.Single(f => f.UserId == "u-two").Codes.Should().Equal("hr", "sales");
    }

    // T-D-04: 冪等 —— 計画どおりに直した属性で再計画すると、食い違いは 0 件である。
    [Fact]
    public void Replanning_after_applying_the_corrections_finds_no_mismatch()
    {
        var codes = new Dictionary<string, IReadOnlySet<string>>
        {
            ["a"] = Codes("sales"),
            ["b"] = Codes("hr"),
            ["c"] = Codes("sales", "hr"),
        };
        var current = new Dictionary<string, string?> { ["a"] = "hr", ["b"] = null, ["c"] = "x" };

        foreach (var f in DepartmentAttributeReconciliation.Plan(codes, current)
                     .Where(f => f.Verdict == DepartmentAttributeVerdict.Mismatch))
            current[f.UserId] = f.Expected;

        var again = DepartmentAttributeReconciliation.Plan(codes, current);
        again.Should().NotContain(f => f.Verdict == DepartmentAttributeVerdict.Mismatch);
        current["c"].Should().Be("x", "未解決の人の属性は計画に触れられていない");
    }
}
