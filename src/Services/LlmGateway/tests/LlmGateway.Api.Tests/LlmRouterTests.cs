using FluentAssertions;
using LlmGateway.Api.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Api.Tests;

// FR-11, ADR-0010, 08_data-egress-policy: 機密区分×ティアの越境マトリクスと用途による呼び出し先切替を検証する。
public class LlmRouterTests
{
    private static LlmEndpointOptions Claude(bool enabled = true, int priority = 10) => new()
    {
        Name = "claude-managed",
        Tier = ProtectionTier.B,
        Provider = "claude",
        Enabled = enabled,
        Priority = priority,
        DefaultModel = "claude-sonnet-4-6",
        Models = ["claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5"]
    };

    private static LlmEndpointOptions SelfHosted(bool enabled = true, int priority = 20) => new()
    {
        Name = "selfhosted-oss",
        Tier = ProtectionTier.A,
        Provider = "selfhosted",
        Enabled = enabled,
        Priority = priority,
        DefaultModel = "oss-llm",
        Models = ["oss-llm"]
    };

    private static LlmEndpointOptions StandardExternal(bool enabled = true, int priority = 5) => new()
    {
        Name = "standard-external",
        Tier = ProtectionTier.C,
        Provider = "claude",
        Enabled = enabled,
        Priority = priority,
        DefaultModel = "std-model",
        Models = ["std-model"]
    };

    private static LlmRouter Build(LlmRoutingOptions options)
        => new(Options.Create(options), NullLogger<LlmRouter>.Instance);

    private static LlmRoutingOptions Opts(params LlmEndpointOptions[] endpoints) => new()
    {
        Endpoints = [.. endpoints],
        PurposeModels = new(StringComparer.OrdinalIgnoreCase)
        {
            ["rag-answer"] = "claude-sonnet-4-6",
            ["analysis"] = "claude-opus-4-8",
            ["diagram-coding"] = "claude-haiku-4-5"
        }
    };

    // 越境マトリクス: public は全ティア許容。
    [Theory]
    [InlineData("public", ProtectionTier.A, true)]
    [InlineData("public", ProtectionTier.C, true)]
    [InlineData("confidential", ProtectionTier.C, false)]
    [InlineData("restricted", ProtectionTier.C, false)]
    [InlineData("confidential", ProtectionTier.B, true)]
    [InlineData("restricted", ProtectionTier.A, true)]
    public void AllowedTiers_FollowsEgressMatrix(string cls, ProtectionTier tier, bool allowed)
    {
        var sensitivity = SensitivityClasses.Parse(cls);
        EgressMatrix.AllowedTiers(sensitivity).Contains(tier).Should().Be(allowed);
    }

    // FR-11: confidential は ティアB（保護契約済み外部API）へ切り替えて送信できる。
    [Fact]
    public void Route_Confidential_SelectsProtectedExternalTierB()
    {
        var router = Build(Opts(Claude(), SelfHosted(enabled: false)));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Confidential, "analysis"));

        decision.Allowed.Should().BeTrue();
        decision.Tier.Should().Be(ProtectionTier.B);
        decision.EndpointName.Should().Be("claude-managed");
        decision.Provider.Should().Be("claude");
        // 用途 analysis → opus
        decision.Model.Should().Be("claude-opus-4-8");
    }

    // FR-11: 許容ティアに送信可能なエンドポイントが無ければ送信を拒否する（縮退）。
    [Fact]
    public void Route_Confidential_WhenOnlyStandardExternal_IsDenied()
    {
        var router = Build(Opts(StandardExternal()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Confidential, "analysis"));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("拒否");
    }

    // FR-11: restricted はティアA（セルフホスト）があればそちらへ切り替えられる。
    [Fact]
    public void Route_Restricted_PrefersEnabledSelfHostedWhenHigherPriority()
    {
        // セルフホストを最優先（priority 1）に設定
        var router = Build(Opts(Claude(priority: 10), SelfHosted(priority: 1)));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Restricted, "analysis"));

        decision.Allowed.Should().BeTrue();
        decision.Tier.Should().Be(ProtectionTier.A);
        decision.Provider.Should().Be("selfhosted");
        decision.Model.Should().Be("oss-llm");
    }

    // FR-11: internal × ティアC は既定（要承認・未許可）では候補から除外される。
    [Fact]
    public void Route_Internal_TierCRequiresApproval_DeniedByDefault()
    {
        var router = Build(Opts(StandardExternal())); // 唯一の候補がティアC

        var decision = router.Route(new RoutingRequest(SensitivityClass.Internal, "rag-answer"));

        decision.Allowed.Should().BeFalse();
    }

    // FR-11: 明示許可（AllowUnapprovedTierC=true）があれば internal はティアCへ送信できる（要承認フラグ付き）。
    [Fact]
    public void Route_Internal_TierCAllowedWhenApprovalGranted()
    {
        var options = Opts(StandardExternal());
        options.AllowUnapprovedTierC = true;
        var router = Build(options);

        var decision = router.Route(new RoutingRequest(SensitivityClass.Internal, "rag-answer"));

        decision.Allowed.Should().BeTrue();
        decision.Tier.Should().Be(ProtectionTier.C);
        decision.RequiresApproval.Should().BeTrue();
    }

    // FR-11: 明示要求モデルがエンドポイントで対応可能ならそれを優先する。
    [Fact]
    public void Route_HonorsRequestedModelWhenSupported()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Public, "rag-answer", "claude-haiku-4-5"));

        decision.Model.Should().Be("claude-haiku-4-5");
    }

    // 08_data-egress-policy「既定は安全側」: 未指定（null/空）・未知の機密区分は Restricted に倒す。
    [Theory]
    [InlineData(null, SensitivityClass.Restricted)]
    [InlineData("", SensitivityClass.Restricted)]
    [InlineData("  ", SensitivityClass.Restricted)]
    [InlineData("unknown-value", SensitivityClass.Restricted)]
    [InlineData("internal", SensitivityClass.Internal)]
    [InlineData("PUBLIC", SensitivityClass.Public)]
    public void Parse_MapsToSafeSide(string? value, SensitivityClass expected)
        => SensitivityClasses.Parse(value).Should().Be(expected);

    // 複数文書の最高機密区分で判定する。
    [Fact]
    public void Highest_TakesMostSensitive()
        => SensitivityClasses.Highest(["public", "confidential", "internal"])
            .Should().Be(SensitivityClass.Confidential);
}
