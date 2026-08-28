using System.Text.RegularExpressions;
using Knowledge.Contracts.Dtos;

namespace AiAnalysisService.Domain;

// FR-07, UC-02: タスク種別（分析・比較・抽出）ごとに LLM プロンプトを組み立てる純粋ロジック。
// いずれの種別でも「参照文書を根拠とし、根拠の無い情報を含めない／出典番号 [1][2] を付す」を厳守させ、
// FR-04 の出典付与（CitationMapper の採番）と一貫させる。
public static partial class AnalysisPromptBuilder
{
    // FR-07: プロンプトインジェクション対策。利用者入力（Instruction）の最大長。
    // これを超える依頼はエンドポイントで 400 とし、本ビルダーでも防御的に切り詰める。
    public const int MaxInstructionLength = 2000;

    public static string Build(AnalysisTaskRequest request, string context)
    {
        // FR-07: タスク種別は明示的に分岐し、未対応の値は黙って「分析」へ落とさず失敗させる。
        var task = request.TaskType switch
        {
            AnalysisTaskType.Analyze =>
                "以下の参照文書を分析し、指示に沿って要点・傾向・洞察をまとめてください。",
            AnalysisTaskType.Compare =>
                "以下の参照文書を比較し、指示に沿って共通点・相違点を対比して示してください。",
            AnalysisTaskType.Extract =>
                "以下の参照文書から、指示された情報を抽出して整理してください。該当が無い項目は「該当なし」と記してください。",
            _ => throw new ArgumentOutOfRangeException(
                nameof(request), request.TaskType, "未対応の分析タスク種別です。"),
        };

        // FR-07: 利用者入力でプロンプト構造（## 見出し）を偽装されないよう無害化する。
        var instruction = SanitizeInstruction(request.Instruction);

        return $"""
            {task}
            参照文書に根拠が無い情報は決して含めず、推測が必要な場合はその旨を明示してください。

            ## 参照文書
            {context}

            ## 指示
            {instruction}

            ## 出力（日本語で、根拠とした箇所に出典番号 [1][2] を付けてください）
            """;
    }

    // FR-07: プロンプトインジェクションの最低限の緩和。
    //   1) 最大長で切り詰める（過大な入力でのプロンプト全体の上書きを防ぐ）。
    //   2) 行頭の見出し記号 (#) を全角 ＃ へ置換し、`## 参照文書` 等のセクション偽装を無害化する。
    private static string SanitizeInstruction(string instruction)
    {
        var trimmed = instruction.Length > MaxInstructionLength
            ? instruction[..MaxInstructionLength]
            : instruction;

        return HeadingMarker().Replace(trimmed, "$1＃");
    }

    [GeneratedRegex(@"(?m)^([ \t]*)#")]
    private static partial Regex HeadingMarker();
}
