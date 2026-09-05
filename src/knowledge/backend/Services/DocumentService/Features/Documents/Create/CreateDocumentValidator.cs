using DocumentService.Domain;
using FluentValidation;

namespace DocumentService.Features.Documents.Create;

// FR-05, FR-06, FR-19, UC-03, SC-05, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0395 / [[IADR-0398]] 決定 1・3・4: 文書の登録の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 4 本であった。
//
// 🔴 **判定の位置が 2 つある。** 移送前は「題名（`:34`）→ **413 の本文上限（`:42`）** →
// 機密区分（`:47`）→ doc_scope の値域（`:54`）→ 個人資料経路（`:56`）」の順で、
// **413 が題名と属性の間に挟まっている**。全部を入口で 1 回走らせると
// 「題名あり・本文 1 MB 超・機密区分なし」が **413 から 400 へ化ける**。
// そこで属性の 3 規則を `RuleSet(AttributesRuleSet)` に入れ、端点が 413 の後ろで
// **第 2 の `Validate(req, o => o.IncludeRuleSets(...))`** を呼ぶ（[[IADR-0398]] 決定 3）。
// 検証器を 2 つに割らないのは、同じ `IValidator<CreateDocumentRequest>` が 2 つになり
// DI 鍵が衝突するためである（IADR-0395 決定 3 が退けた形）。
//
// 🔴 **ハザード**: `Validate(req)`（オプション無し）は**名前つき集合を走らせない**。
// 端点の第 2 の呼び出しを消してもコンパイルも起動も通り、属性が**黙って無検証**になる。
// `CreateDocumentValidatorTests.DefaultRuleSet_DoesNotRunAttributeRules` と
// 端点側の 413 / 400 の**対**の試験がこれを固定する。
//
// 🔴 **鍵は必ず明示する。** 推論名は `Title`（PascalCase）であり、移送前の `title` と一致しない。
internal sealed class CreateDocumentValidator : AbstractValidator<CreateDocumentRequest>
{
    // FR-06, UC-03: 移送前のガード節が返していた鍵と本文。**この 2 つが応答の契約である。**
    internal const string TitleKey = "title";
    internal const string TitleRequiredMessage = "タイトルは必須です。";

    // FR-19, ADR-0054, [[IADR-0270]] 決定 2: 一般経路での個人資料の作成を拒否する。
    // 台帳（PrivateNote）を持たない個人資料ができると容量算入（FR-19）から漏れる。
    internal const string PrivateNoteRouteMessage =
        "個人資料（doc_scope=private-note）はこの経路では作成できません。"
        + "/private-notes（SC-19）または Obsidian 同期から作成してください。";

    // 🔴 **413（本文上限）の後ろで走らせる規則の集合名。** 端点はこの定数で第 2 の集合を呼ぶ
    // （文字列を 2 箇所に書かない。IADR-0395 決定 5 の区切り文字と同じ作法）。
    internal const string AttributesRuleSet = "attributes";

    public CreateDocumentValidator()
    {
        // 位置①（入口）。FR-06, UC-03: タイトルは必須。
        // **述語は元のまま写す**（`NotEmpty()` へ置き換えない。空白のみの題名の扱いが変わる）。
        RuleFor(r => r.Title)
            .Must(t => !string.IsNullOrWhiteSpace(t))
            .OverridePropertyName(TitleKey)
            .WithMessage(TitleRequiredMessage);

        // 位置②（413 の後ろ）。宣言順 = 移送前のガード節の順
        // （機密区分 → doc_scope の値域 → 個人資料経路）。
        RuleSet(AttributesRuleSet, () =>
        {
            RuleFor(r => r.Attributes).Confidentiality();
            RuleFor(r => r.Attributes).DocScope();
            RuleFor(r => r.Attributes)
                .Must(a => !DocumentAttributes.IsPrivateNote(a))
                .OverridePropertyName(DocumentAttributes.DocScopeKey)
                .WithMessage(PrivateNoteRouteMessage);
        });
    }
}
