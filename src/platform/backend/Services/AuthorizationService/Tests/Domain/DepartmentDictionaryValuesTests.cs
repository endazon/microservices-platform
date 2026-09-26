using AuthorizationService.Domain;
using AuthorizationService.Features.Authz;
using AwesomeAssertions;

namespace AuthorizationService.Tests.Domain;

// FR-05, FR-09, SC-09, 計画 ADR-0116 決定 3, [[IADR-0476]] (#1609): 属性辞書の `department` の許可値を realm から導く純関数。
[Trait("TestKind", "Unit")]
public class DepartmentDictionaryValuesTests
{
    private static readonly DepartmentDomainReading Realm = DepartmentDomainReading.Of(["sales", "engineering", "hr", "sales"]);

    [Fact]
    public void A_reading_is_ordinal_ordered_and_distinct()
        => Realm.Codes.Should().Equal("engineering", "hr", "sales");

    [Theory]
    [InlineData("department", true)]
    [InlineData("Department", true)] // 辞書のキーの一意性と同じ大小文字無視
    [InlineData("clearance", false)]
    [InlineData(null, false)]
    public void Only_department_is_derived(string? key, bool expected)
        => DepartmentDictionaryValues.IsDerived(key).Should().Be(expected);

    // 🔴 読めたら realm のコード、読めなければ保存済みの値（空へ倒さない）。
    [Fact]
    public void Effective_values_fall_back_to_the_stored_ones_when_the_realm_is_unknown()
    {
        DepartmentDictionaryValues.Effective(["finance"], Realm).Should().Equal("engineering", "hr", "sales");
        DepartmentDictionaryValues.Effective(["finance"], DepartmentDomainReading.Unknown).Should().Equal("finance");
        DepartmentDictionaryValues.Effective(null, DepartmentDomainReading.Unknown).Should().BeEmpty();
    }

    [Fact]
    public void The_source_is_realm_unknown_or_hand_held()
    {
        DepartmentDictionaryValues.SourceOf("department", Realm).Should().Be("realm");
        DepartmentDictionaryValues.SourceOf("department", DepartmentDomainReading.Unknown).Should().Be("realm-unavailable");
        DepartmentDictionaryValues.SourceOf("clearance", Realm).Should().BeNull();
    }

    [Theory]
    [InlineData(new string[0], true)]                                  // 空 ＝ realm から導く
    [InlineData(new[] { "sales", "hr", "engineering" }, true)]         // 同じ集合（並び違い）
    [InlineData(new[] { "engineering", "hr" }, false)]                 // 手で消す
    [InlineData(new[] { "engineering", "hr", "sales", "finance" }, false)] // 手で足す
    [InlineData(new[] { "Engineering", "hr", "sales" }, false)]        // 序数（大小文字違いは別の値）
    public void Requests_are_accepted_only_when_empty_or_equal_to_the_effective_set(string[] requested, bool expected)
        => DepartmentDictionaryValues.RequestAccepted(requested, Realm.Codes).Should().Be(expected);

    // 当てはめ: 変わったときだけ true（保存し直しの要否）。手で持つキーには触れない。
    [Fact]
    public void Apply_replaces_only_department_values_and_reports_whether_anything_changed()
    {
        var department = AttributeDefinition.Create("department", "部門", ["finance", "legal"], false, AttributeScope.User);
        var clearance = AttributeDefinition.Create("clearance", "取扱", ["public"], false, AttributeScope.User);

        AttributeDictionary.Apply([department, clearance], Realm).Should().BeTrue();
        department.AllowedValues.Should().Equal("engineering", "hr", "sales");
        clearance.AllowedValues.Should().Equal("public");
        AttributeDictionary.Apply([department, clearance], Realm).Should().BeFalse("2 回目は変わらない（保存し直さない）");
    }
}
