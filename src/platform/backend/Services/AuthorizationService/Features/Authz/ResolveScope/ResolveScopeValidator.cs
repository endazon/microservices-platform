using AuthorizationService.Domain;
using FluentValidation;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Features.Authz.ResolveScope;

// FR-05, FR-21, UC-05, 計画 ADR-0004 / ADR-0036 D-07 /
// ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0395 決定 2 /
// [[IADR-0398]] 決定 1 (b)・9: 権限スコープ解決の入力規則。
// 従前は `Endpoint.cs` 内の手書きガード節 1 本であった。
//
// 🔴 **この検証器は鍵を持たない**（`OverridePropertyName` を書かない）。
// 応答の鍵（`errors`）は sink（`AuthzEndpoints.ValidationProblem`）が持つ。
// その sink は**本サービスの 8 サイトが共有**しており、うち 6 サイトは
// `AbacValidation` / `UserAssignmentValidation` の配列を**全件**載せる形 β である。
// **鍵の正を検証器へ持たせると、同じ鍵の正が 2 つになる**（[[IADR-0398]] 決定 1 (b)）。
//
// 🔴 **述語は元のまま写す。** `PolicyAction.IsValid` は `All.Contains` の**完全一致**であり、
// `Trim` も小文字化もしない（McpServer の `TryParseKind` とは**逆**である —— 名前が似ているからと
// 揃えない）。`IsEnumName` や自前の集合比較へ置き換えると `"Read"` / `" read "` の扱いが割れる。
//
// 🔴 **値域は `Domain/PolicyAction` が持つ。** メッセージの列挙もそこから組み立てる ——
// ここへ書き写すと、値域が増えたときにメッセージだけが黙って古くなる。
internal sealed class ResolveScopeValidator : AbstractValidator<AccessScopeRequest>
{
    // 🔴 `const` にできない（`string.Join` は定数式ではない）。移送前と**同じ式**から作る
    // （`Endpoint.cs:24-25`）。`static readonly` にするのは `SubmitFeedbackValidator` と同じ理由である。
    internal static readonly string ActionInvalidMessage =
        $"action は {string.Join(" / ", PolicyAction.All)} のいずれかである必要があります。";

    public ResolveScopeValidator()
    {
        RuleFor(r => r.Action)
            .Must(PolicyAction.IsValid)
            .WithMessage(ActionInvalidMessage);
    }
}
