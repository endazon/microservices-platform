using AwesomeAssertions;
using SampleService.Domain;

namespace SampleService.Tests.Domain;

// テンプレート: ドメインの単体テスト。**外部依存を立てない**（Domain/ 自体が外部依存ゼロである）。
//
// 置き場: **Tests/Domain/**（IADR-0334 決定 1・3）。鏡写しの相手は Domain/SampleAggregate.cs で、
// 本体のパスをそのまま Tests/ 配下へ写す。鏡写しの相手は Features/ と Domain/ に限らない ——
// 実サービスでは Infrastructure/<Sub>/・Common/<Sub>/・Domain/Ports/ も同じ規則で写す
// （雛形はそれらの実体を持たないので、空の枠は作らない。IADR-0321）。
public class SampleAggregateTests
{
    [Theory]
    [InlineData("sample", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsNamed_は空白のみの名前を名付け済みと見なさない(string name, bool expected)
    {
        var aggregate = new SampleAggregate(Guid.NewGuid(), name);

        aggregate.IsNamed.Should().Be(expected);
    }
}
