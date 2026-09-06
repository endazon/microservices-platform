using FluentValidation;

namespace DocumentService.Features.ObsidianSync.Push;

// FR-20, UC-11, SC-20, ADR-0037 決定 7・8, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0395 / [[IADR-0398]] 決定 1・3・9: push（新規作成・更新）の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 2 本であった。
//
// 🔴 **判定の位置が 2 つある。** 移送前は「入口の 4 項（`:34`）→ **413 の本文上限（`:43`）** →
// 新規／更新の分岐 → 更新側で **404**（`:100` / `:105`）→ `baseVersion`（`:109`）→ 409（`:114`）」
// の順である。全部を入口で 1 回走らせると **不存在の noteId ＋ baseVersion なし**が
// **404 から 400 へ化ける**。そこで `baseVersion` の規則を `RuleSet(BaseVersionRuleSet)` に入れ、
// 端点が更新分岐の 404 の後ろで**第 2 の `Validate(req, o => o.IncludeRuleSets(...))`** を呼ぶ
// （[[IADR-0398]] 決定 3。`Create` と同じ形）。検証器を 2 つに割らないのは、同じ
// `IValidator<PushNoteRequest>` が 2 つになり DI 鍵が衝突するためである（IADR-0395 決定 3）。
//
// 🔴 **ハザード**: `Validate(req)`（オプション無し）は**名前つき集合を走らせない**。
// 端点の第 2 の呼び出しを消してもコンパイルも起動も通り、`baseVersion` が**黙って無検証**になる。
// `PushNoteValidatorTests.DefaultRuleSet_DoesNotRunBaseVersionRule` と、端点側の
// 404 / 400 の**対**（`Push_UnknownNoteWithoutBaseVersion_Returns404` /
// `Push_ExistingNoteWithoutBaseVersion_Returns400WithBaseVersionKey`）がこれを固定する。
//
// 🔴 **鍵は必ず明示する。** 入口の鍵は `errors`（属性名ではない）—— メッセージが 3 項目に
// またがるため、どれか一方の名前を鍵にできない。推論名は `Title` になる。
internal sealed class PushNoteValidator : AbstractValidator<PushNoteRequest>
{
    // FR-20, ADR-0037 決定 8: 移送前のガード節が返していた鍵と本文。**この 2 つが応答の契約である。**
    internal const string ErrorsKey = "errors";
    internal const string RequiredFieldsMessage =
        "title / vaultPath / edits（1 件以上・content 必須）を指定してください。";

    // FR-20, ADR-0037 決定 7: 競合はサーバで解決しない。更新には最後に見た版が要る。
    internal const string BaseVersionKey = "baseVersion";
    internal const string BaseVersionRequiredMessage = "既存資料の更新には baseVersion が必須です。";

    // 🔴 **更新分岐の 404 の後ろで走らせる規則の集合名。** 端点はこの定数で第 2 の集合を呼ぶ
    // （文字列を 2 箇所に書かない。IADR-0395 決定 5 の区切り文字と同じ作法）。
    internal const string BaseVersionRuleSet = "baseVersion";

    public PushNoteValidator()
    {
        // 位置①（入口。401 の後ろ・413 の前）。
        // 🔴 **述語の粒度を写す。** 移送前は 4 項を **1 本の `||`** で見て 1 つのメッセージを返していた。
        // 4 本の `RuleFor` に割ると、全部不正な要求で失敗が 4 件になり粒度が変わる
        // （`Errors[0]` を採るので本文の 1 件は変わらないが、等価性の G 軸が壊れる）。
        // **短絡評価も保つ** —— `Edits` が null のとき `req.Edits.Any(...)` を評価してはならない
        // （`||` の左から 3 項目めで確定する。順を入れ替えると `NullReferenceException` になる）。
        RuleFor(r => r.Title)
            .Must((req, _) => !(string.IsNullOrWhiteSpace(req.Title)
                || string.IsNullOrWhiteSpace(req.VaultPath)
                || req.Edits is not { Count: > 0 }
                || req.Edits.Any(e => e.Content is null)))
            .OverridePropertyName(ErrorsKey)
            .WithMessage(RequiredFieldsMessage);

        // 位置②（更新分岐の 404 の後ろ）。**新規作成（`NoteId` が null）では走らない** ——
        // 端点が更新側の枝でだけ第 2 の呼び出しを行う。
        RuleSet(BaseVersionRuleSet, () =>
        {
            RuleFor(r => r.BaseVersion)
                .Must(v => v is not null)
                .OverridePropertyName(BaseVersionKey)
                .WithMessage(BaseVersionRequiredMessage);
        });
    }
}
