using AwesomeAssertions;
using LlmGateway.Domain.Pricing;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests.Domain.Pricing;

// T-1, FR-11, NFR-21, ADR-0044, IADR-0466 決定 2 (#1111): 月次予算の上限の起動時検証。
// **未設定は正常である**（計画が金額を定めていない現在の状態）。誤った設定は配備の失敗として表に出す ——
// 綴り違いの用途は `llm_cost_total` 側に同じ `llm_purpose` が現れず、式が空のまま「設定した」と読めるため。
[Trait("TestKind", "Unit")]
public class LlmBudgetOptionsValidatorTests
{
    private static LlmBudgetOptionsValidator Validator()
    {
        var routing = new LlmRoutingOptions();
        routing.PurposeModels["rag-answer"] = "claude-sonnet-5";
        routing.PurposeModels["trade-decision"] = "claude-sonnet-5";
        return new LlmBudgetOptionsValidator(new Static<LlmRoutingOptions>(routing));
    }

    private static LlmBudgetOptions With(params (string Purpose, decimal Limit)[] limits)
    {
        var options = new LlmBudgetOptions();
        foreach (var (purpose, limit) in limits)
            options.MonthlyLimits[purpose] = limit;
        return options;
    }

    // T-1a: 🔴 **未設定（空）は成功する。** ここが失敗すると、金額を決めていない現状で起動できなくなり、
    // 「起動させるために何か数字を入れる」圧力が生まれる（計画が禁じた「実装側で金額を決める」への近道）。
    [Fact]
    public void 未設定は成功する()
        => Validator().Validate(null, new LlmBudgetOptions()).Succeeded.Should().BeTrue();

    // T-1b: PurposeModels のキーと default は既知の用途として受け付ける（大文字小文字は区別しない）。
    [Fact]
    public void 既知の用途とdefaultは受け付ける()
        => Validator().Validate(null, With(("rag-answer", 1m), ("TRADE-DECISION", 2m), ("default", 3m)))
            .Succeeded.Should().BeTrue();

    // T-1c: 未知の用途は落とす（綴り違い・計器の集約先 other を含む）。
    [Theory]
    [InlineData("rag-answr")]
    [InlineData("other")]
    public void 未知の用途は落とす(string purpose)
    {
        var result = Validator().Validate(null, With((purpose, 1m)));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(purpose).And.Contain("既知の用途ではありません");
    }

    // T-1d: 0 以下の金額は落とす（常に超過＝恒常発火を作る）。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ゼロ以下の金額は落とす(int limit)
    {
        var result = Validator().Validate(null, With(("rag-answer", limit)));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("0 以下");
    }

    private sealed class Static<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
