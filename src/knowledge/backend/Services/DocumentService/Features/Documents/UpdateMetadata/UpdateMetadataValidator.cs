using FluentValidation;

namespace DocumentService.Features.Documents.UpdateMetadata;

// FR-05, FR-06, FR-19, UC-03, SC-05, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0395 / [[IADR-0398]] 決定 1・4: メタデータ更新の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 2 本であった。
//
// **題名の規則は無い** —— この口は属性とタグだけを更新するためである（要求 DTO に `Title` が無い）。
// 🔴 **検証は `FindAsync` より前**（`UpdateDocumentValidator` と同じ理由）。
// 🔴 **doc_scope の不変性検査は端点に残る**（既存値が要る。[[IADR-0398]] 決定 8）。
internal sealed class UpdateMetadataValidator : AbstractValidator<UpdateMetadataRequest>
{
    public UpdateMetadataValidator()
    {
        // 宣言順 = 移送前のガード節の順（confidentiality → doc_scope）。
        // **鍵とメッセージは `DocumentAttributeRules` 経由で `Domain/DocumentAttributes` が持つ**
        // （3 操作で 1 つの判定。書き分けない）。
        RuleFor(r => r.Attributes).Confidentiality();
        RuleFor(r => r.Attributes).DocScope();
    }
}
