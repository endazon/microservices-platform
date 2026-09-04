using FluentValidation;
using Knowledge.Contracts.Dtos;

namespace DashboardService.Features.Dashboard.RecordEvent;

// FR-10, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0376:
// 利用イベント記録の入力規則。従前は Endpoint.cs 内の手書きガード節 1 本であった。
//
// 🔴 **振る舞いを変えない移送である。** 同じ 400・同じ本文を返す。
// 判定そのものは契約側の `UsageEventType.IsValid` が持ち、ここへ複写しない
// （大小の揺れを許すかどうかは契約の知識であり、正規化する `Normalize` と対で保たれている）。
internal sealed class RecordUsageEventValidator : AbstractValidator<UsageEventRequest>
{
    // FR-10: 元のガード節が返していた本文の文字列。**これが応答の契約である。**
    internal const string EventTypeInvalidMessage = "eventType must be 'search' or 'answer'";

    public RecordUsageEventValidator()
    {
        RuleFor(r => r.EventType)
            .Must(UsageEventType.IsValid)
            .WithMessage(EventTypeInvalidMessage);
    }
}
