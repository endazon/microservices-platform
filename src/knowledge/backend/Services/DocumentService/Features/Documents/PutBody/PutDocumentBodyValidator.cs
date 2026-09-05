using FluentValidation;

namespace DocumentService.Features.Documents.PutBody;

// FR-21, UC-03, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0395 /
// [[IADR-0398]] 決定 1: 文書本文の直接受け入れの入力規則。
// 従前は Endpoint.cs 内の手書きガード節 1 本であった。
//
// 🔴 **述語は `is null` である。`NotEmpty()` にも `IsNullOrWhiteSpace` にもしない** ——
// **空文字の本文は有効な要求**であり（本文を空にする更新）、置き換えると 400 に化ける。
//
// 🔴 **1 MB 超（413）はここに入れない。** 移送前も認可（404）の**後ろ**に居り、しかも 400 ではない
// （FR-21 受け入れ基準 ⑥ が status を名指ししている）。入れると 413 が 400 になり、
// **他人の文書に対する本文サイズの情報が認可より先に漏れる**。
internal sealed class PutDocumentBodyValidator : AbstractValidator<UpdateDocumentBodyRequest>
{
    // FR-21: 移送前のガード節が返していた鍵と本文。**この 2 つが応答の契約である。**
    internal const string BodyKey = "body";
    internal const string BodyRequiredMessage = "本文は必須です。";

    public PutDocumentBodyValidator()
    {
        RuleFor(r => r.Body)
            .Must(b => b is not null)
            .OverridePropertyName(BodyKey)
            .WithMessage(BodyRequiredMessage);
    }
}
