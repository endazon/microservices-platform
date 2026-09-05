using DocumentService.Domain;
using FluentValidation;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.Tags.Create;

// FR-09, SC-09, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0395 /
// [[IADR-0398]] 決定 1・4: タグ辞書への追加の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 1 本であった。
//
// 🔴 **述語は元のまま写す** —— `Tag.Normalize`（= `Trim`）を掛けた後の `IsNullOrEmpty` である
// （`NotEmpty()` へ置き換えると空白のみの名前が通る）。
//
// 🔴 **重複（409）はここに入れない。** DB の照会結果であり入力検証ではない。しかも状態コードが違う
// （SC-09「新しい名前は既存値と重複しない」→ 409）。[[IADR-0398]] 決定 8。
//
// **`AddDocumentTagValidator` / `RenameTagValidator` と共有しない**（移送前も 3 複製。同 決定 4）。
internal sealed class CreateTagValidator : AbstractValidator<CreateTagRequest>
{
    // FR-09, SC-09: 移送前のガード節が返していた鍵と本文。**この 2 つが応答の契約である。**
    internal const string NameKey = "name";
    internal const string NameRequiredMessage = "タグ名は必須です。";

    public CreateTagValidator()
    {
        RuleFor(r => r.Name)
            .Must(n => !string.IsNullOrEmpty(Tag.Normalize(n ?? string.Empty)))
            .OverridePropertyName(NameKey)
            .WithMessage(NameRequiredMessage);
    }
}
