using FluentValidation;

namespace DocumentService.Features.Documents.Update;

// FR-05, FR-06, FR-19, UC-03, SC-05, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0395 / [[IADR-0398]] 決定 1・4: 文書の編集の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 3 本であった。
//
// 🔴 **検証は `FindAsync` より前である。** 移送前も 3 本とも取得の前に居た ——
// **不存在の文書 ID への空題名更新は 400 であり、404 ではない**。ハンドラの後ろへ動かすと
// 400 が 404 に化ける（`RenameEdgeType` は逆向きに「後ろへ置く」ことが仕様だった。
// **同じ形に見えるからと揃えない**）。端点側にも同じ注記を置いた。
//
// 🔴 **doc_scope の不変性検査（`DocScopeChangedProblemOrNull`）はここに持ってこない。**
// あちらは既存文書の属性が要るため取得の後ろに残る（[[IADR-0398]] 決定 8）。
// **値域検証とは別の検査である**（あちらは「知らない値か」、こちらは「作成時の値から動いたか」）。
internal sealed class UpdateDocumentValidator : AbstractValidator<UpdateDocumentRequest>
{
    // FR-06, UC-03: 移送前のガード節が返していた鍵と本文。**この 2 つが応答の契約である。**
    internal const string TitleKey = "title";
    internal const string TitleRequiredMessage = "タイトルは必須です。";

    public UpdateDocumentValidator()
    {
        // 宣言順 = 移送前のガード節の順（title → confidentiality → doc_scope）。
        // 端点は `Errors[0]` を採るので、入れ替えると複数違反時の本文が変わる。
        RuleFor(r => r.Title)
            .Must(t => !string.IsNullOrWhiteSpace(t))
            .OverridePropertyName(TitleKey)
            .WithMessage(TitleRequiredMessage);

        // FR-05, IADR-0047: 更新でも機密区分を必須検証する（属性は全置換のため）。
        RuleFor(r => r.Attributes).Confidentiality();
        // FR-19, ADR-0054: doc_scope の値域検証（未知値は 400。欠落は拒否しない）。
        RuleFor(r => r.Attributes).DocScope();
    }
}
