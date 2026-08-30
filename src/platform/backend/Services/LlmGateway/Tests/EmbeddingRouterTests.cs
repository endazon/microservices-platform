using AwesomeAssertions;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests;

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

    // FR-02, FR-03, #992 案 2, [[IADR-0313]]: 決定的ローカル埋め込み（ティアA・Priority=5）を足した構成。
    // 🔴 **越境判定（EmbeddingEgress / Route）は 1 バイトも変えていない。** 変わるのは
    //   「ティアA に置ける実装が増えた」ことだけである。ここではその帰結を固定する。
    private static EmbeddingRouter BuildWithDeterministic()
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
                    Enabled = false, Priority = 20
                },
                new EmbeddingEndpointOptions
                {
                    Name = "deterministic-local", Tier = ProtectionTier.A, Provider = "deterministic-embedding",
                    Model = "deterministic-hash-v1", Dimensions = 1024,
                    Collection = "knowledge_chunks_deterministic_v1", Enabled = true, Priority = 5
                }
            ]
        });
        return new EmbeddingRouter(options, NullLogger<EmbeddingRouter>.Instance);
    }

    // 🔴 **索引もクエリも同じエンドポイント＝同じコレクションへ寄る。**
    // 片方だけ寄ると索引と問い合わせが別空間になり、検索は静かに 0 件になる（門が測りたいものが測れない）。
    [Theory]
    [InlineData(SensitivityClass.Public, EmbeddingRoutePurpose.Index)]
    [InlineData(SensitivityClass.Internal, EmbeddingRoutePurpose.Index)]
    [InlineData(SensitivityClass.Confidential, EmbeddingRoutePurpose.Index)]
    [InlineData(SensitivityClass.Restricted, EmbeddingRoutePurpose.Index)]
    [InlineData(SensitivityClass.Public, EmbeddingRoutePurpose.Query)]
    [InlineData(SensitivityClass.Restricted, EmbeddingRoutePurpose.Query)]
    public void Route_DeterministicEnabled_SelectsItForIndexAndQuery(
        SensitivityClass sensitivity, EmbeddingRoutePurpose purpose)
    {
        var decision = BuildWithDeterministic().Route(new EmbeddingRoutingRequest(sensitivity, purpose));

        decision.Allowed.Should().BeTrue();
        decision.Provider.Should().Be("deterministic-embedding");
        decision.Tier.Should().Be(ProtectionTier.A);
        decision.Collection.Should().Be("knowledge_chunks_deterministic_v1");
        decision.Dimensions.Should().Be(1024);
    }

    // 🔴 **越境の既定値そのものは動いていない。** 機密区分 × 許容ティアの表を固定する
    // （受け入れ基準 8。ここが変わっていたら、本作業は「CI のために fail-closed を緩めた」ことになる）。
    [Theory]
    [InlineData(SensitivityClass.Public, "A,B")]
    [InlineData(SensitivityClass.Internal, "A,B")]
    [InlineData(SensitivityClass.Confidential, "A")]
    [InlineData(SensitivityClass.Restricted, "A")]
    public void AllowedTiers_IsUnchanged(SensitivityClass sensitivity, string expected)
        => string.Join(",", EmbeddingEgress.AllowedTiers(sensitivity).OrderBy(t => t))
            .Should().Be(expected);
}
