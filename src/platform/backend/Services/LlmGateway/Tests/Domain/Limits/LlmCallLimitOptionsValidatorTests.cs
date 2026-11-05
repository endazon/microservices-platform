using AwesomeAssertions;
using LlmGateway.Domain.Limits;
using LlmGateway.Domain.Routing;

namespace LlmGateway.Tests.Domain.Limits;

// NFR-18, IADR-0672 (#2404): 送る前の上限の設定の起動時検証。
// 0 以下の上限は段 2 で全件を止め、綴り違いの用途は「設定した」と読めるのに 1 件も数えない。どちらも起動で落とす。
[Trait("TestKind", "Unit")]
public class LlmCallLimitOptionsValidatorTests
{
    private static LlmCallLimitOptionsValidator Validator()
    {
        var routing = new LlmRoutingOptions();
        routing.PurposeModels["rag-answer"] = "claude-sonnet-5-5";
        return new LlmCallLimitOptionsValidator(new TestClaudeProviders.StaticOptionsMonitor<LlmRoutingOptions>(routing));
    }

    [Fact]
    public void 既定の設定は成功し_段の既定はReportである()
    {
        var options = new LlmCallLimitOptions();

        Validator().Validate(null, options).Succeeded.Should().BeTrue();
        options.Mode.Should().Be(LlmCallLimitMode.Report);
        options.Default.DailyCostLimit.Should().BeNull("費用の上限の金額の既定は置かない");
        options.Default.PurposeDailyCostLimits.Should().BeEmpty();
    }

    [Fact]
    public void 既知の用途とdefaultとotherの費用の上限は受け付ける()
    {
        var options = new LlmCallLimitOptions();
        options.Default.PurposeDailyCostLimits["RAG-ANSWER"] = 1m;
        options.Default.PurposeDailyCostLimits["default"] = 1m;
        options.Callers["graph-service"] = new LlmCallerLimits { PurposeDailyCostLimits = { ["other"] = 1m } };

        Validator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("max")]
    [InlineData("prompt")]
    [InlineData("rpm")]
    [InlineData("purposeRpm")]
    [InlineData("cost")]
    [InlineData("purposeCost")]
    [InlineData("unknownPurpose")]
    [InlineData("callerRpm")]
    public void 不正な値は失敗する(string which)
    {
        var o = new LlmCallLimitOptions();
        switch (which)
        {
            case "max": o.MaxOutputTokens = 0; break;
            case "prompt": o.MaxPromptChars = -1; break;
            case "rpm": o.Default.RequestsPerMinute = 0; break;
            case "purposeRpm": o.Default.PurposeRequestsPerMinute = -5; break;
            case "cost": o.Default.DailyCostLimit = 0m; break;
            case "purposeCost": o.Default.PurposeDailyCostLimits["rag-answer"] = -1m; break;
            case "unknownPurpose": o.Default.PurposeDailyCostLimits["rag-answr"] = 1m; break;
            case "callerRpm": o.Callers["graph-service"] = new LlmCallerLimits { RequestsPerMinute = 0 }; break;
        }

        Validator().Validate(null, o).Failed.Should().BeTrue();
    }

    [Fact]
    public void 呼び手の上書きは書かなかった項目に既定を使い_用途の費用の表は既定に重ねる()
    {
        var o = new LlmCallLimitOptions();
        o.Default.DailyCostLimit = 5m;
        o.Default.PurposeDailyCostLimits["rag-answer"] = 1m;
        o.Default.PurposeDailyCostLimits["default"] = 2m;
        o.Callers["graph-service"] = new LlmCallerLimits
        {
            RequestsPerMinute = 10,
            PurposeDailyCostLimits = { ["rag-answer"] = 3m },
        };

        var e = EffectiveCallerLimits.For(o, "GRAPH-SERVICE");

        e.RequestsPerMinute.Should().Be(10);
        e.PurposeRequestsPerMinute.Should().Be(120);
        e.DailyCostLimit.Should().Be(5m);
        e.PurposeDailyCostLimits["rag-answer"].Should().Be(3m);
        e.PurposeDailyCostLimits["default"].Should().Be(2m);
        EffectiveCallerLimits.For(o, "retrieval-service").RequestsPerMinute.Should().Be(300);
    }
}
