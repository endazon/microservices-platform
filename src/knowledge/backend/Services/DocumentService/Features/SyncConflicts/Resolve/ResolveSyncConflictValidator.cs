using FluentValidation;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.SyncConflicts.Resolve;

// FR-20, SC-20 主要素 5, ADR-0037 決定 7, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// [[IADR-0398]] 決定 1, #1442: 競合の解決方法の入力規則。
//
// 🔴 **選べるのは 3 択だけである**（`SyncConflictResolutions.Selectable`）。
// `client` は**プラグイン側で解決された競合をサーバが閉じるときの記録用の値**であり、
// 利用者が選ぶ値ではない —— 端点から受け付けると「自動解決した」記録を外から作れてしまう。
// **自動解決（後勝ち）の値はそもそも存在しない**（決定 7）。
internal sealed class ResolveSyncConflictValidator : AbstractValidator<ResolveSyncConflictRequest>
{
    internal const string ResolutionKey = "resolution";
    internal const string ResolutionInvalidMessage =
        "解決方法は local / server / both のいずれかを指定してください。";

    public ResolveSyncConflictValidator()
    {
        RuleFor(r => r.Resolution)
            .Must(v => v is not null && SyncConflictResolutions.IsSelectable(v))
            .OverridePropertyName(ResolutionKey)
            .WithMessage(ResolutionInvalidMessage);
    }
}
