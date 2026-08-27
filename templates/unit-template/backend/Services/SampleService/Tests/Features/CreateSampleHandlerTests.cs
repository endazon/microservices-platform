using AwesomeAssertions;
using SampleService.Features.Samples.Create;

namespace SampleService.Tests.Features;

// テンプレート: 受け入れ基準 → テストの写像。テスト名またはコメントに起点 ID（FR/UC/SC）を残す
// （.claude/rules/traceability.md）。テストはスライスを鏡写しにした Tests/Features/ へ置く（IADR-0282）。
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
