using DocumentService.Domain;
using FluentValidation;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.Tags.Rename;

// FR-09, SC-09, #635, IADR-0153, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 /
// IADR-0395 / [[IADR-0398]] 決定 1・4: タグ辞書の改名の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 1 本であった。
//
// 🔴 **検証は辞書の取得（`FirstOrDefaultAsync`）より前である。** 移送前もそうだった ——
// **不存在のタグ ID への空名改名は 400 であり 404 ではない**。
// GraphService の `RenameEdgeType` は**逆**（取得の後ろが仕様）なので、
// **名前が似ているからと揃えない**（IADR-0395 決定 2: 実行は従前のガード節が居た位置）。
//
// 🔴 **重複（409）はここに入れない**（DB の照会結果。状態コードも違う。[[IADR-0398]] 決定 8）。
internal sealed class RenameTagValidator : AbstractValidator<RenameTagRequest>
{
    // FR-09, SC-09: 移送前のガード節が返していた鍵と本文。**この 2 つが応答の契約である。**
    internal const string NameKey = "name";
    internal const string NameRequiredMessage = "タグ名は必須です。";

    public RenameTagValidator()
    {
        RuleFor(r => r.Name)
            .Must(n => !string.IsNullOrEmpty(Tag.Normalize(n ?? string.Empty)))
            .OverridePropertyName(NameKey)
            .WithMessage(NameRequiredMessage);
    }
}
