using AwesomeAssertions;
using DocumentService.Features.SyncDevices.Issue;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.SyncDevices.Issue;

// FR-20, UC-11, SC-20, ADR-0037 決定 11, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / [[IADR-0398]] 決定 1・9: 同期トークン発行の入力検証の**振る舞い同値**（#1278 PR-B）。
[Trait("TestKind", "Unit")]
public class IssueSyncDeviceValidatorTests
{
    private readonly IssueSyncDeviceValidator _validator = new();

    // 陽性対照。
    [Fact]
    public void ValidRequest_Passes()
    {
        var result = _validator.Validate(new CreateSyncDeviceRequest("MacBook"));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 🔴 鍵とメッセージの両方を、**定数とリテラルの両方**へ当てる。
    // `OverridePropertyName` を消すと鍵が `DeviceName` になってここで止まる。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankDeviceName_FailsWithOriginalKeyAndMessage(string deviceName)
    {
        var result = _validator.Validate(new CreateSyncDeviceRequest(deviceName));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].PropertyName.Should().Be(IssueSyncDeviceValidator.DeviceNameKey);
        IssueSyncDeviceValidator.DeviceNameKey.Should().Be("deviceName");
        result.Errors[0].ErrorMessage.Should()
            .Be(IssueSyncDeviceValidator.DeviceNameRequiredMessage);
        IssueSyncDeviceValidator.DeviceNameRequiredMessage.Should().Be("端末名は必須です。");
    }
}
