using FluentValidation;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.PrivateNotes.Create;

// FR-19, UC-11, SC-19, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 /
// IADR-0395 / [[IADR-0398]] 決定 1: 個人資料の作成の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 1 本であった。
//
// 🔴 **述語は元のまま写す**（`IsNullOrWhiteSpace` を `NotEmpty()` へ置き換えない）。
//
// 🔴 **鍵は必ず明示する。** 推論名は `Title`（PascalCase）であり、移送前の `title` と一致しない。
//
// **`CreateDocumentValidator` と共有しない。** メッセージのリテラルは同じ（`"タイトルは必須です。"`）
// だが、移送前も 2 箇所がそれぞれ書いていた —— 共有化は「振る舞いを変えない」枠を超える整理である
// （[[IADR-0398]] 決定 4 がタグ名の 3 複製について述べたのと同じ理由）。
//
// 🔴 **容量（100% 到達時の拒否）はここに入れない。** DB の照会結果であり入力検証ではない。
// しかも状態コードが違う（`PrivateNoteEndpoints.QuotaExceededProblem`）。[[IADR-0398]] 決定 8。
internal sealed class CreatePrivateNoteValidator : AbstractValidator<CreatePrivateNoteRequest>
{
    // FR-19, SC-19: 移送前のガード節が返していた鍵と本文。**この 2 つが応答の契約である。**
    internal const string TitleKey = "title";
    internal const string TitleRequiredMessage = "タイトルは必須です。";

    public CreatePrivateNoteValidator()
    {
        RuleFor(r => r.Title)
            .Must(t => !string.IsNullOrWhiteSpace(t))
            .OverridePropertyName(TitleKey)
            .WithMessage(TitleRequiredMessage);
    }
}
