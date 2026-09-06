using FluentValidation;

namespace DocumentService.Features.ObsidianSync.Move;

// FR-20, UC-11, SC-20, ADR-0037 決定 2・7, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0395 / [[IADR-0398]] 決定 1・9: リネーム（vaultPath の更新）の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 2 本であった。
//
// 🔴 **宣言順が応答の契約である。** 移送前は `vaultPath` → `version` の順にガード節が並び、
// **最初の違反でその場で `return` していた** —— `vaultPath` も `version` も欠けた要求の応答は
// `{"errors":{"vaultPath":[...]}}` の 1 鍵 1 件である（形 α）。端点は
// `ValidationProblems.FirstViolation` で先頭 1 件だけを載せる。順を入れ替えると
// `SyncValidationProblemContractTests.Move_BlankVaultPathAndNoVersion_Returns400WithVaultPathOnly`
// が止める。
//
// 🔴 **鍵は必ず明示する。** 推論名は `VaultPath` / `Version`（PascalCase）であり、
// 移送前の `vaultPath` / `version` と一致しない。**型では止まらない。**
//
// 🔴 **述語は元のまま写す。** `IsNullOrWhiteSpace` を `NotEmpty()` へ、
// `req.Version is not { } version` を `NotNull()` へ置き換えない（`NotEmpty()` は `0` も落とすので
// **`version=0` の要求が 400 に化ける**）。
internal sealed class MoveNoteValidator : AbstractValidator<MoveNoteRequest>
{
    // FR-20, ADR-0037 決定 2: 移送前のガード節が返していた鍵と本文。**この 2 つが応答の契約である。**
    internal const string VaultPathKey = "vaultPath";
    internal const string VaultPathRequiredMessage = "移動先の vaultPath を指定してください。";

    // FR-20, ADR-0037 決定 7: 版は進めないが、古い認識のまま名前を動かさせないため必須である。
    internal const string VersionKey = "version";
    internal const string VersionRequiredMessage = "リネームには version（最後に見た版）が必須です。";

    public MoveNoteValidator()
    {
        RuleFor(r => r.VaultPath)
            .Must(p => !string.IsNullOrWhiteSpace(p))
            .OverridePropertyName(VaultPathKey)
            .WithMessage(VaultPathRequiredMessage);

        RuleFor(r => r.Version)
            .Must(v => v is not null)
            .OverridePropertyName(VersionKey)
            .WithMessage(VersionRequiredMessage);
    }
}
