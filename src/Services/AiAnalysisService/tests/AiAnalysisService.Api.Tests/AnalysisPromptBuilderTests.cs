using AiAnalysisService.Api.Services;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Api.Tests;

// FR-07, UC-02: タスク種別ごとにプロンプトが切り替わり、出典・根拠の制約が常に含まれることを検証する。
public class AnalysisPromptBuilderTests
{
    private const string Context = "[1] 文書A\n抜粋A\n";

    [Theory]
    [InlineData(AnalysisTaskType.Analyze, "分析")]
    [InlineData(AnalysisTaskType.Compare, "比較")]
    [InlineData(AnalysisTaskType.Extract, "抽出")]
    public void Build_SelectsTaskSpecificInstruction(AnalysisTaskType type, string keyword)
    {
        var req = new AnalysisTaskRequest("対象を処理して", type);

        var prompt = AnalysisPromptBuilder.Build(req, Context);

        prompt.Should().Contain(keyword);
        prompt.Should().Contain(req.Instruction);
        prompt.Should().Contain(Context);
    }

    [Fact]
    public void Build_AlwaysRequiresCitationsAndGrounding()
    {
        var req = new AnalysisTaskRequest("要約して", AnalysisTaskType.Analyze);

        var prompt = AnalysisPromptBuilder.Build(req, Context);

        // 出典番号の指示と「根拠の無い情報を含めない」制約が常に含まれること
        prompt.Should().Contain("[1]");
        prompt.Should().Contain("根拠");
    }
}
