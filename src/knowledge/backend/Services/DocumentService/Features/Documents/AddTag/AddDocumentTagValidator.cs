using DocumentService.Domain;
using FluentValidation;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.Documents.AddTag;

// FR-18, SC-03, SC-05, SC-09, ADR-0063 決定 1〜3, IADR-0364, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0395 / [[IADR-0398]] 決定 1・4: 文書へのタグ付与の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 1 本であった。
//
// 🔴 **述語は元のまま写す** —— `Tag.Normalize`（= `Trim`）を掛けた**後**の `IsNullOrEmpty` である。
// `NotEmpty()` へ置き換えると、空白のみのタグ名（`" "`）が通ってしまう。
//
// 🔴 **「辞書に無い名前は 400」（`UnknownTagsProblem`）はここに入れない。** あれは辞書照会の**結果**であり
// 入力検証ではない。しかも**認可の後ろ**に置くことが仕様である（端点の ★認可★ 注記 ——
// 辞書照合を先にすると書けない主体に「そのタグは辞書に無い」という情報が返る）。[[IADR-0398]] 決定 8。
//
// **タグ名の規則は `Tags/Create` `Tags/Rename` と共有しない。** 移送前も 3 箇所が同じ 4 行を
// それぞれ書いており、共有化は「振る舞いを変えない移送」の枠を超える整理である（[[IADR-0398]] 決定 4）。
internal sealed class AddDocumentTagValidator : AbstractValidator<AddDocumentTagRequest>
{
    // FR-18, SC-09: 移送前のガード節が返していた鍵と本文。**この 2 つが応答の契約である。**
    internal const string NameKey = "name";
    internal const string NameRequiredMessage = "タグ名は必須です。";

    public AddDocumentTagValidator()
    {
        RuleFor(r => r.Name)
            .Must(n => !string.IsNullOrEmpty(Tag.Normalize(n ?? string.Empty)))
            .OverridePropertyName(NameKey)
            .WithMessage(NameRequiredMessage);
    }
}
