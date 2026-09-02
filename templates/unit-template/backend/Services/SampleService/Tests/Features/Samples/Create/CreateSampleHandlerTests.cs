using AwesomeAssertions;
using SampleService.Features.Samples.Create;

namespace SampleService.Tests.Features.Samples.Create;

// テンプレート: 受け入れ基準 → テストの写像。テスト名またはコメントに起点 ID（FR/UC/SC）を残す
// （.claude/rules/traceability.md）。
//
// 置き場: **本体の鏡写し**である（IADR-0334 決定 1・3）。CreateSampleHandler を直接呼ぶので、
// その型が定義されたフォルダ Features/Samples/Create/ をそのまま Tests/ 配下へ写す。
// **段（集約 / 操作）まで写す**——Tests/Features/ 直下に平置きしない。
// 名前空間はフォルダへ追随させる（同決定 5）。
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
