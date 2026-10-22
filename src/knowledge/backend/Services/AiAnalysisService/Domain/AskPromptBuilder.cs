namespace AiAnalysisService.Domain;

// FR-04, UC-01 / NFR・IADR-0580（Refs GHSA-5h4q-4m2j-rjhm）: 自然文の質問への回答のプロンプト。
// 参照文書と質問は PromptTrustBoundary の囲みに入れ、指示は囲みの外に置く。
public static class AskPromptBuilder
{
    public static string Build(string question, string context)
        => Build(question, context, PromptTrustBoundary.NewBoundary());

    internal static string Build(string question, string context, string boundary)
        => $"""
            以下の参照文書を根拠に、質問に答えてください。根拠のない情報は含めないでください。
            {PromptTrustBoundary.Rules(boundary)}

            ## 参照文書
            {PromptTrustBoundary.Wrap(PromptTrustBoundary.DocumentsTag, context, boundary)}

            ## 質問
            {PromptTrustBoundary.Wrap(PromptTrustBoundary.UserInputTag, question, boundary)}

            ## 回答（日本語で、出典番号 [1][2] を付けてください）
            """;
}
