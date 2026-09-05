using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;

namespace Platform.Shared.Infrastructure.Tests.Contracts;

// FR-16, FR-09, SC-12, SC-17, ADR-0062 決定 2, IADR-0385 (#1243):
// 集合値の利用者属性（`tags` / `projects`）の符号化を固定する。
//
// 🔴 **本クラスが規則の唯一の所在を守っている。** 連結（Keycloak → 契約）と分割（契約 → 判定）は
// 別サービスに置かれ、互いを直接参照できない。**規則が 2 つに割れると、割れたことは
// 「登録者が持つタグが少ない」という静かな過小としてしか現れない**（#1243 の実測がそれである）。
[Trait("TestKind", "Unit")]
public class UserAttributeEncodingTests
{
    // 🔴 集合値キーは 2 つだけである。**単一値キーを足すと階段ポリシーが静かに壊れる。**
    [Theory]
    [InlineData("tags", true)]
    [InlineData("projects", true)]
    [InlineData("TAGS", true)]
    // 陰性対照（対で置く）: これらは 1 値であり、区切り文字を見出してはならない。
    [InlineData("clearance", false)]
    [InlineData("department", false)]
    [InlineData("project", false)]
    [InlineData("roles", false)]
    public void 集合値キーはタグと参加プロジェクトだけである(string key, bool expected)
        => UserAttributeEncoding.IsSetValued(key).Should().Be(expected);

    // Keycloak の多値配列 → 契約の線上表現。
    [Fact]
    public void 多値は区切りで連結される()
        => UserAttributeEncoding.Join(["sales", "hr"]).Should().Be("sales,hr");

    // 空・空白のみの要素は落とす（Keycloak は空文字を持てる）。
    [Fact]
    public void 空白だけの要素は連結に含めない()
        => UserAttributeEncoding.Join(["sales", "", "  ", null, "hr"]).Should().Be("sales,hr");

    [Fact]
    public void 値が無ければ空文字になる()
        => UserAttributeEncoding.Join([]).Should().BeEmpty();

    // 🔴 **2 つの保存形が同じ集合へ読める**（#1243 の設計の要点）。
    [Theory]
    [InlineData("sales,hr")]      // 正準（多値配列を連結した形）
    [InlineData("sales, hr")]     // 人手入力の揺れ
    [InlineData("sales hr")]      // 空白区切り
    [InlineData("hr,sales")]      // 🔴 順序は意味を持たない
    public void 線上表現は同じ集合へ分割される(string wire)
        => UserAttributeEncoding.Split(wire).Should().BeEquivalentTo(["sales", "hr"]);

    [Fact]
    public void 単一値はその一要素の集合になる()
        => UserAttributeEncoding.Split("sales").Should().BeEquivalentTo(["sales"]);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(",,")]
    public void 値が無ければ空集合になる(string? wire)
        => UserAttributeEncoding.Split(wire).Should().BeEmpty();

    // 大文字小文字を問わない（realm・取り込み経路で綴りが揺れる）。
    [Fact]
    public void 分割後の照合は大文字小文字を問わない()
        => UserAttributeEncoding.Split("Sales,HR").Should().Contain("sales").And.Contain("hr");

    // 🔴 **往復が閉じている。** 閉じていないと、書き戻し（正準形の配列）と読み戻しで値が変わる。
    [Fact]
    public void 連結と分割は往復で閉じる()
    {
        var stored = new[] { "sales", "hr" };

        var wire = UserAttributeEncoding.Join(stored);
        var back = UserAttributeEncoding.SplitOrdered(wire);

        back.Should().BeEquivalentTo(stored, o => o.WithStrictOrdering());
        UserAttributeEncoding.Join(back).Should().Be(wire);
    }

    // `SplitOrdered` は**並びを保つ**（Keycloak へ書き戻す配列は並びが観測される）。
    [Fact]
    public void 並びつきの分割は入力の順序を保つ()
        => UserAttributeEncoding.SplitOrdered("hr, sales, finance")
            .Should().BeEquivalentTo(["hr", "sales", "finance"], o => o.WithStrictOrdering());
}
