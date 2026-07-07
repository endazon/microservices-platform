using FluentAssertions;
using LlmGateway.Api.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Api.Tests;

// FR-02, FR-05, ADR-0016, ADR-0017: 埋め込みルーターの機密区分ティア判定・fail-closed を検証する。
public class EmbeddingRouterTests
{
    private static EmbeddingRouter Build(bool selfHostedEnabled)
    {
        var options = Options.Create(new EmbeddingRoutingOptions
        {
            Endpoints =
            [
                new EmbeddingEndpointOptions
                {
                    Name = "voyage-managed", Tier = ProtectionTier.B, Provider = "voyage",
                    Model = "voyage-3.5", Dimensions = 1024, Collection = "knowledge_chunks_voyage_3_5",
                    Enabled = true, Priority = 10
                },
                new EmbeddingEndpointOptions
                {
                    Name = "selfhosted-ruri", Tier = ProtectionTier.A, Provider = "selfhosted-embedding",
                    Model = "ruri-v3", Dimensions = 768, Collection = "knowledge_chunks_ruri_v3",
                    Enabled = selfHostedEnabled, Priority = 20
                }
            ]
        });
        return new EmbeddingRouter(options, NullLogger<EmbeddingRouter>.Instance);
    }

    // public 索引は既定外部経路（voyage・ティアB・1024次元）へ。
    [Fact]
    public void Route_PublicIndex_SelectsVoyage()
    {
        var decision = Build(selfHostedEnabled: false)
            .Route(new EmbeddingRoutingRequest(SensitivityClass.Public, EmbeddingRoutePurpose.Index));

        decision.Allowed.Should().BeTrue();
        decision.Provider.Should().Be("voyage");
        decision.Tier.Should().Be(ProtectionTier.B);
        decision.Dimensions.Should().Be(1024);
        decision.Collection.Should().Be("knowledge_chunks_voyage_3_5");
    }

    // confidential 索引はティアA固定。セルフホスト未有効なら送信を拒否（fail-closed）。voyage は候補にならない。
    [Theory]
    [InlineData(SensitivityClass.Confidential)]
    [InlineData(SensitivityClass.Restricted)]
    public void Route_HighSensitivityIndex_SelfHostedDisabled_Denies(SensitivityClass sensitivity)
    {
        var decision = Build(selfHostedEnabled: false)
            .Route(new EmbeddingRoutingRequest(sensitivity, EmbeddingRoutePurpose.Index));

        decision.Allowed.Should().BeFalse();
        decision.Provider.Should().BeNull();
        decision.Reason.Should().Contain("fail-closed");
    }

    // confidential 索引はセルフホスト有効時のみティアA（ruri・768次元）へ。外部（voyage）へは向かわない。
    [Fact]
    public void Route_ConfidentialIndex_SelfHostedEnabled_SelectsSelfHosted()
    {
        var decision = Build(selfHostedEnabled: true)
            .Route(new EmbeddingRoutingRequest(SensitivityClass.Confidential, EmbeddingRoutePurpose.Index));

        decision.Allowed.Should().BeTrue();
        decision.Provider.Should().Be("selfhosted-embedding");
        decision.Tier.Should().Be(ProtectionTier.A);
        decision.Dimensions.Should().Be(768);
        decision.Collection.Should().Be("knowledge_chunks_ruri_v3");
    }

    // クエリ埋め込みは機密区分に依らず既定外部経路（voyage・1024次元）へ固定（検索対象コレクションと整合）。
    [Fact]
    public void Route_Query_AlwaysSelectsDefaultExternal()
    {
        var decision = Build(selfHostedEnabled: false)
            .Route(new EmbeddingRoutingRequest(SensitivityClass.Restricted, EmbeddingRoutePurpose.Query));

        decision.Allowed.Should().BeTrue();
        decision.Provider.Should().Be("voyage");
        decision.Dimensions.Should().Be(1024);
    }
}
