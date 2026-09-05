using FluentValidation;
using GraphService.Domain;
using Knowledge.Contracts.Dtos;

namespace GraphService.Features.EdgeTypes.Create;

// FR-17, SC-09, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0395:
// 辺の型の追加の入力規則。従前は Endpoint.cs 内の手書きガード節 2 本であった。
//
// 🔴 **振る舞いを変えない移送である。** 同じ 400・同じ本文（`{ "error": "..." }`）を返すため:
//   1. **規則の宣言順を元のガード節の順に揃える**（name → layer）。呼び出し側が `Errors[0]` を
//      採るので、順序を入れ替えると両方違反したときの本文が変わる。
//   2. **述語も元のまま写す。** `NotEmpty()` へ置き換えない —— 移送前は
//      `EdgeType.Normalize`（`Trim()`）を掛けた**後**の空判定であり、`NotEmpty()` の空判定と
//      一致するかはライブラリの版に依存する。移送で確かめるべきは等価性なので、
//      確かめられない置き換えを混ぜない（IADR-0393 決定 3 と同じ作法）。
//   3. **`null` の扱いも元のまま。** 移送前は `req.Name ?? string.Empty` / `req.Layer ?? string.Empty`
//      で null を空へ縮退させてから判定していた。DTO は非 null 宣言だが、JSON の欠落で null が
//      入り得るため、縮退をここでも保つ。
internal sealed class CreateEdgeTypeValidator : AbstractValidator<CreateEdgeTypeRequest>
{
    // FR-17: 元のガード節が返していた本文の文字列。**この 2 本が応答の契約である。**
    internal const string NameRequiredMessage = "name_required";
    internal const string InvalidLayerMessage = "invalid_layer";

    public CreateEdgeTypeValidator()
    {
        // FR-17, SC-09: 名前は必須（正規化後に空なら不可）。
        RuleFor(r => r.Name)
            .Must(n => !string.IsNullOrEmpty(EdgeType.Normalize(n ?? string.Empty)))
            .WithMessage(NameRequiredMessage);

        // FR-17, SC-09: 層は辞書の語彙のいずれか。
        RuleFor(r => r.Layer)
            .Must(l => EdgeTypeLayer.IsValid(l ?? string.Empty))
            .WithMessage(InvalidLayerMessage);
    }
}
