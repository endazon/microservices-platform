using FluentAssertions;
using LlmGateway.Api.Composable.Adapters;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Api.Tests;

// T-20, FR-11, IADR-0109 (#394): OpenAI 互換 finish_reason → 契約の正準語彙（CompletionStopReasons）の
// 写像表そのものを固定する。未知値を既定値へ潰さないこと（潰すと「正常終了」に見えて最悪である）と、
// 大小文字非依存であることが要点。
public class OpenAiFinishReasonTests
{
    // T-20a: 既知語彙は正準語彙へ写像する。
    [Theory]
    [InlineData("stop", CompletionStopReasons.EndTurn)]
    [InlineData("length", CompletionStopReasons.MaxTokens)]
    [InlineData("content_filter", CompletionStopReasons.Refusal)]
    [InlineData("tool_calls", CompletionStopReasons.ToolUse)]
    [InlineData("function_call", CompletionStopReasons.ToolUse)]   // OpenAI で非推奨の旧語彙
    public void Map_KnownFinishReason_NormalizesToCanonicalVocabulary(string finishReason, string expected)
    {
        var (stopReason, unmapped) = OpenAiFinishReasons.Map(finishReason);

        stopReason.Should().Be(expected);
        unmapped.Should().BeFalse();
    }

    // T-20a: 大小文字は問わない（CompletionStopReasons.IsRefusal と同じ方針）。
    [Theory]
    [InlineData("STOP", CompletionStopReasons.EndTurn)]
    [InlineData("Content_Filter", CompletionStopReasons.Refusal)]
    public void Map_IsCaseInsensitive(string finishReason, string expected)
    {
        OpenAiFinishReasons.Map(finishReason).StopReason.Should().Be(expected);
    }

    // T-20b: 未知値は原文のまま透過し、未写像であることを呼び出し側へ伝える（ログ用）。
    [Fact]
    public void Map_UnknownFinishReason_PassesThroughAndReportsUnmapped()
    {
        var (stopReason, unmapped) = OpenAiFinishReasons.Map("some_future_reason");

        stopReason.Should().Be("some_future_reason");
        unmapped.Should().BeTrue();
        // 既定値（正常終了）へ倒さないことが要点。倒すと拒否・打ち切りが正常終了に見える。
        stopReason.Should().NotBe(CompletionStopReasons.EndTurn);
    }

    // T-20c: 欠落・null・空白は「未対応プロバイダと同じ null」。未知語彙ではないので警告対象にしない。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Map_MissingFinishReason_IsNullAndNotReportedAsUnmapped(string? finishReason)
    {
        var (stopReason, unmapped) = OpenAiFinishReasons.Map(finishReason);

        stopReason.Should().BeNull();
        unmapped.Should().BeFalse();
    }
}
