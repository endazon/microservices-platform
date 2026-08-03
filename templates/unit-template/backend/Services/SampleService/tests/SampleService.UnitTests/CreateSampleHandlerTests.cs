using AwesomeAssertions;
using SampleService.Application;

namespace SampleService.UnitTests;

// テンプレート: 受け入れ基準 → テストの写像。テスト名またはコメントに起点 ID（FR/UC/SC）を残す
// （.claude/rules/traceability.md）。写像規約の詳細は #453 のテスト戦略に従う。
public class CreateSampleHandlerTests
{
    [Fact]
    public void Handle_名前を与えるとイベントに反映される()
    {
        var clock = TimeProvider.System;

        var evt = CreateSampleHandler.Handle(new CreateSample("sample"), clock);

        evt.Name.Should().Be("sample");
        evt.Id.Should().NotBeEmpty();
    }
}
