using AiAnalysisService.Domain;
using FluentValidation;
using Knowledge.Contracts.Dtos;

namespace AiAnalysisService.Features.Analysis.Analyze;

// FR-07, UC-02, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0377:
// 分析依頼の入力規則。従前は Endpoint.cs 内の手書きガード節 2 本であった。
//
// 🔴 **振る舞いを変えない移送である。** 同じ 400・同じ本文（`{ "error": "..." }`）を返すため、
// 次の 2 点を守る（参照実装 SubmitFeedbackValidator と同じ作法）:
//   1. **規則の宣言順を元のガード節の順に揃える**（必須 → 上限長）。FluentValidation は既定で
//      全規則を走らせるが、呼び出し側が `Errors[0]` を採ることで元の「最初の違反で返す」と
//      同じ文字列になる。順序を入れ替えると本文が変わる。
//   2. **述語も元のまま持ち上げる。** 必須の判定を `NotEmpty()` へ置き換えない ——
//      元は `string.IsNullOrWhiteSpace` であり、`NotEmpty()` の空判定と一致するかは
//      ライブラリの版に依存する。**移送で確かめるべきは等価性なので、述語を写す。**
internal sealed class AnalyzeRequestValidator : AbstractValidator<AnalysisTaskRequest>
{
    // FR-07: 元のガード節が返していた本文の文字列。**この 2 本が応答の契約である。**
    internal const string InstructionRequiredMessage = "instruction is required";

    // 🔴 `const` にできない（`int` の補間は定数式にならない。CS0133）。移送前と同じ文字列を
    // **同じ式から**作るために `static readonly` にする。数値を直書きすると、上限を変えたときに
    // メッセージだけが古いまま残る。
    internal static readonly string InstructionTooLongMessage =
        $"instruction must be {AnalysisPromptBuilder.MaxInstructionLength} characters or fewer";

    public AnalyzeRequestValidator()
    {
        // FR-07: 指示は必須。空依頼は受け付けない。
        RuleFor(r => r.Instruction)
            .Must(instruction => !string.IsNullOrWhiteSpace(instruction))
            .WithMessage(InstructionRequiredMessage);

        // FR-07: プロンプトインジェクション緩和。過大な指示は受け付けない。
        // **null 安全に書く** —— 移送前は 1 本目のガード節で返り切っていたが、
        // FluentValidation は全規則を走らせるため、ここへ null が来得る。
        RuleFor(r => r.Instruction)
            .Must(instruction => instruction is not { Length: > AnalysisPromptBuilder.MaxInstructionLength })
            .WithMessage(InstructionTooLongMessage);
    }
}
