using FluentValidation;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.SyncDevices.Issue;

// FR-20, UC-11, SC-20, ADR-0037 決定 11, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0395 / [[IADR-0398]] 決定 1: 同期トークンの発行（端末登録）の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 1 本であった。
//
// 🔴 **述語は元のまま写す**（`IsNullOrWhiteSpace` を `NotEmpty()` へ置き換えない）。
//
// 🔴 **鍵は必ず明示する。** 推論名は `DeviceName`（PascalCase）であり、
// 移送前の `deviceName` と一致しない。**型では止まらない。**
internal sealed class IssueSyncDeviceValidator : AbstractValidator<CreateSyncDeviceRequest>
{
    // FR-20, SC-20: 移送前のガード節が返していた鍵と本文。**この 2 つが応答の契約である。**
    internal const string DeviceNameKey = "deviceName";
    internal const string DeviceNameRequiredMessage = "端末名は必須です。";

    public IssueSyncDeviceValidator()
    {
        RuleFor(r => r.DeviceName)
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .OverridePropertyName(DeviceNameKey)
            .WithMessage(DeviceNameRequiredMessage);
    }
}
