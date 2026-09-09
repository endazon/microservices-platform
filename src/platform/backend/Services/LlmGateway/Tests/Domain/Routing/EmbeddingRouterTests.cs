using AwesomeAssertions;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests.Domain.Routing;

// FR-02, FR-05, ADR-0016, ADR-0017: 埋め込みルーターの機密区分ティア判定・fail-closed を検証する。
[Trait("TestKind", "Unit")]
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

    // ---- FR-03, ADR-0016, ADR-0017, [[IADR-0422]] 決定 2 (#336): 検索クエリ送信先の固定（測定用） ----
    //
    // ADR-0017 の nDCG@10 の A/B は、**クエリの埋め込みを検索対象コレクションと同じモデルへ寄せられないと
    // 成立しない**。ここで固定するのは「寄せられること」と「寄せても越境が広がらないこと」の 2 つである。

    private static EmbeddingRouter BuildWithProfile(string? queryProfile, bool selfHostedEnabled = true)
    {
        var options = Options.Create(new EmbeddingRoutingOptions
        {
            QueryProfile = queryProfile,
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

    // FR-03, ADR-0016, IADR-0422: 未設定（空文字・null）なら現行と同一の決定になる（既定の挙動は動かない）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Route_QueryProfile未設定なら既定の送信先が変わらない(string? profile)
    {
        var decision = BuildWithProfile(profile)
            .Route(new EmbeddingRoutingRequest(SensitivityClass.Public, EmbeddingRoutePurpose.Query));

        decision.Allowed.Should().BeTrue();
        decision.EndpointName.Should().Be("voyage-managed");
        decision.Collection.Should().Be("knowledge_chunks_voyage_3_5");
        decision.Dimensions.Should().Be(1024);
    }

    // FR-03, ADR-0016, ADR-0017, IADR-0422: 名指しすると、そのエンドポイントのモデル・次元・
    // コレクションが選ばれる（＝Ruri コレクションを Ruri のクエリ埋め込みで検索できる）。
    [Fact]
    public void Route_QueryProfileを指定するとそのエンドポイントが選ばれる()
    {
        var decision = BuildWithProfile("selfhosted-ruri")
            .Route(new EmbeddingRoutingRequest(SensitivityClass.Public, EmbeddingRoutePurpose.Query));

        decision.Allowed.Should().BeTrue();
        decision.EndpointName.Should().Be("selfhosted-ruri");
        decision.Provider.Should().Be("selfhosted-embedding");
        decision.Model.Should().Be("ruri-v3");
        decision.Dimensions.Should().Be(768);
        decision.Collection.Should().Be("knowledge_chunks_ruri_v3");
    }

    // FR-02, FR-05, ADR-0016, IADR-0422: 🔴 **取り込み（Index）には効かない。**
    // 文書側の送信先は機密区分が決めるものであり、測定用の切替口で動かしてはならない。
    [Fact]
    public void Route_QueryProfileはIndexに効かない()
    {
        var decision = BuildWithProfile("selfhosted-ruri")
            .Route(new EmbeddingRoutingRequest(SensitivityClass.Public, EmbeddingRoutePurpose.Index));

        decision.EndpointName.Should().Be("voyage-managed");
    }

    // FR-05, ADR-0016, IADR-0422: 🔴 **プロファイルは越境を広げられない。**
    // 絞り込みは `EmbeddingEgress.AllowedTiers` と `Enabled` の篩を**通った後**に効くので、
    // 高機密の取り込みで外部（ティアB）を名指ししても deny（fail-closed）のままである。
    // **これが変異試験 M-4（適用点を篩の前へ移す）で赤になる試験である。**
    [Theory]
    [InlineData(SensitivityClass.Confidential)]
    [InlineData(SensitivityClass.Restricted)]
    public void Route_QueryProfileは高機密の越境を開かない(SensitivityClass sensitivity)
    {
        var router = BuildWithProfile("voyage-managed", selfHostedEnabled: false);

        // 取り込み: 高機密はティアA のみ。プロファイルの有無に関係なく拒否される。
        var index = router.Route(new EmbeddingRoutingRequest(sensitivity, EmbeddingRoutePurpose.Index));
        index.Allowed.Should().BeFalse();
        index.Reason.Should().Contain("fail-closed");
    }

    // FR-03, ADR-0016, IADR-0422: 無効なエンドポイントを名指ししたときは**既定へ落とさず拒否する**。
    // 黙って voyage へ落ちると、Ruri を測ったつもりで voyage を測ることになる
    // （通常の構成では EmbeddingRoutingOptionsValidator が起動時に落とす）。
    [Fact]
    public void Route_無効なQueryProfileは既定へ落ちずに拒否される()
    {
        var decision = BuildWithProfile("selfhosted-ruri", selfHostedEnabled: false)
            .Route(new EmbeddingRoutingRequest(SensitivityClass.Public, EmbeddingRoutePurpose.Query));

        decision.Allowed.Should().BeFalse();
        decision.EndpointName.Should().BeNull();
        decision.Reason.Should().Contain("selfhosted-ruri");
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
