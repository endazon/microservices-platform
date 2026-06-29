using KnowledgePlatform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Api.Services;

// FR-07, UC-02: タスク種別（分析・比較・抽出）ごとに LLM プロンプトを組み立てる純粋ロジック。
// いずれの種別でも「参照文書を根拠とし、根拠の無い情報を含めない／出典番号 [1][2] を付す」を厳守させ、
// FR-04 の出典付与（CitationMapper の採番）と一貫させる。
public static class AnalysisPromptBuilder
{
    public static string Build(AnalysisTaskRequest request, string context)
    {
        var task = request.TaskType switch
        {
            AnalysisTaskType.Compare =>
                "以下の参照文書を比較し、指示に沿って共通点・相違点を対比して示してください。",
            AnalysisTaskType.Extract =>
                "以下の参照文書から、指示された情報を抽出して整理してください。該当が無い項目は「該当なし」と記してください。",
            _ =>
                "以下の参照文書を分析し、指示に沿って要点・傾向・洞察をまとめてください。",
        };

        return $"""
            {task}
            参照文書に根拠が無い情報は決して含めず、推測が必要な場合はその旨を明示してください。

            ## 参照文書
            {context}

            ## 指示
            {request.Instruction}

            ## 出力（日本語で、根拠とした箇所に出典番号 [1][2] を付けてください）
            """;
    }
}
