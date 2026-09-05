using FluentValidation;
using GraphService.Domain;
using Knowledge.Contracts.Dtos;

namespace GraphService.Features.EdgeTypes.Rename;

// FR-17, SC-09, ADR-0033 決定 9, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0395: 辺の型の改名の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 1 本であった。
//
// 🔴 **検証を端点の先頭へ上げてはならない。** 移送前は `db.EdgeTypes.FirstOrDefaultAsync` で
// 型を引いた**後**に空名を弾いていた —— **不存在の型 ID への空名改名は 404 である。**
// ハンドラ先頭で回すと 404 が 400 に化ける（移送は振る舞いを変えない作業である）。
// 端点側にも同じ注記を置いた。`RenameEdgeTypeOrderTests` がこの帰結を固定する。
//
// **述語は元のまま写す**（`EdgeType.Normalize` を掛けた後の空判定。`NotEmpty()` へ
// 置き換えない。理由は `CreateEdgeTypeValidator` の注記 2 と同じ）。
internal sealed class RenameEdgeTypeValidator : AbstractValidator<RenameEdgeTypeRequest>
{
    // FR-17: 元のガード節が返していた本文の文字列。**これが応答の契約である。**
    internal const string NameRequiredMessage = "name_required";

    public RenameEdgeTypeValidator()
    {
        // FR-17, SC-09: 名前は必須（正規化後に空なら不可）。
        RuleFor(r => r.Name)
            .Must(n => !string.IsNullOrEmpty(EdgeType.Normalize(n ?? string.Empty)))
            .WithMessage(NameRequiredMessage);
    }
}
