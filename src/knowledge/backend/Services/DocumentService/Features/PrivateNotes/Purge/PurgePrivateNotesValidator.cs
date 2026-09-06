using FluentValidation;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.PrivateNotes.Purge;

// FR-19, UC-11, SC-19, ADR-0037 決定 20, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0395 / [[IADR-0398]] 決定 1: 完全削除（即時・復元不可）の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 1 本であった。
//
// 🔴 **述語は元のまま写す。** `req.Ids is not { Count: > 0 }` は **null も空リストも同じに扱う**
// （`NotEmpty()` へ置き換えると null のときの失敗メッセージが既定文言に化ける）。
//
// 🔴 **鍵は必ず明示する。** 推論名は `Ids`（PascalCase）であり、移送前の `ids` と一致しない。
//
// 🔴 **「存在しない ID」（404）と「削除済みでない」（409）はここに入れない。** DB の照会結果であり
// 入力検証ではない。しかも状態コードが違う（[[IADR-0398]] 決定 8）。
internal sealed class PurgePrivateNotesValidator : AbstractValidator<PurgePrivateNotesRequest>
{
    // FR-19, SC-19: 移送前のガード節が返していた鍵と本文。**この 2 つが応答の契約である。**
    internal const string IdsKey = "ids";
    internal const string IdsRequiredMessage = "完全削除する資料の ID を 1 件以上指定してください。";

    public PurgePrivateNotesValidator()
    {
        RuleFor(r => r.Ids)
            .Must(ids => ids is { Count: > 0 })
            .OverridePropertyName(IdsKey)
            .WithMessage(IdsRequiredMessage);
    }
}
