using AwesomeAssertions;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests.Domain.Routing;

// FR-03, FR-05, ADR-0016, ADR-0092 決定 2, [[IADR-0467]] (#336): 検索が名乗る**読み先コレクション**
// （`TargetCollection`）による絞り込み。
//
// 🔴 守りは 3 つで、それぞれ別の変異で赤になる。
//   - 絞り込みは越境判定と `Enabled` の篩の**後**（M-3: 前へ移すと無効なエンドポイントを名指しで選べる）
//   - **Index には効かない**（M-4: 効かせると文書の送信先を呼び出し側が選べる）
//   - 候補が消えたら**既定へ落とさず拒否**（黙って別モデルで埋めない）
[Trait("TestKind", "Unit")]
public class EmbeddingRouterTargetCollectionTests
{
    private const string Voyage = "knowledge_chunks_voyage_3_5";
    private const string Ruri = "knowledge_chunks_ruri_v3";

    private static EmbeddingRouter Build(bool ruriEnabled, string? queryProfile = null) =>
        new(Options.Create(new EmbeddingRoutingOptions
        {
            QueryProfile = queryProfile,
            Endpoints =
            [
                new EmbeddingEndpointOptions
                {
                    Name = "voyage-managed", Tier = ProtectionTier.B, Provider = "voyage",
                    Model = "voyage-3.5", Dimensions = 1024, Collection = Voyage, Enabled = true, Priority = 10
                },
                new EmbeddingEndpointOptions
                {
                    Name = "selfhosted-ruri", Tier = ProtectionTier.A, Provider = "selfhosted-embedding",
                    Model = "ruri-v3", Dimensions = 768, Collection = Ruri, Enabled = ruriEnabled, Priority = 20
                }
            ]
        }), NullLogger<EmbeddingRouter>.Instance);

    private static EmbeddingRoutingRequest Query(string? target) =>
        new(SensitivityClass.Public, EmbeddingRoutePurpose.Query, target);

    // T-R-01: 名乗ったコレクションのエンドポイントが選ばれる（優先度では voyage が先でも ruri になる）。
    // これがティア A のコレクションをティア A のモデルで引く経路である（ADR-0092 決定 2）。
    [Fact]
    public void Queryで読み先を名乗るとそのコレクションのエンドポイントが選ばれる()
    {
        var decision = Build(ruriEnabled: true).Route(Query(Ruri));

        decision.Allowed.Should().BeTrue();
        decision.EndpointName.Should().Be("selfhosted-ruri");
        decision.Tier.Should().Be(ProtectionTier.A);
        decision.Collection.Should().Be(Ruri);
        decision.Dimensions.Should().Be(768);
    }

    // T-R-02: 🔴 **不変。** 名乗らない（null / 空 / 空白）なら従来と同一の決定（優先度順＝voyage）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 読み先を名乗らなければ従来と同じ決定になる(string? target)
    {
        var router = Build(ruriEnabled: true);

        var named = router.Route(Query(target));
        var legacy = router.Route(new EmbeddingRoutingRequest(SensitivityClass.Public, EmbeddingRoutePurpose.Query));

        named.Should().BeEquivalentTo(legacy);
        named.EndpointName.Should().Be("voyage-managed");
    }

    // T-R-03: 🔴 **無効なエンドポイントのコレクションを名乗っても開かない**（篩の後に効く）。
    // 既定へ落ちずに拒否し、理由に読み先を書く。変異 M-3（`Enabled` の篩の前へ移す）はここで赤になる。
    [Fact]
    public void 無効なエンドポイントのコレクションを名乗っても開かず既定へも落ちない()
    {
        var decision = Build(ruriEnabled: false).Route(Query(Ruri));

        decision.Allowed.Should().BeFalse();
        decision.EndpointName.Should().BeNull();
        decision.Reason.Should().Contain("fail-closed").And.Contain(Ruri);
    }

    // T-R-04: 存在しないコレクションも同じく拒否（別モデルで黙って埋めない）。
    [Fact]
    public void 存在しないコレクションを名乗ると拒否される()
        => Build(ruriEnabled: true).Route(Query("knowledge_chunks_nowhere")).Allowed.Should().BeFalse();

    // T-R-05: 🔴 **Index には効かない。** 高機密の本文はティア A へ、公開の本文は優先度どおりに送られ、
    // 呼び出し側がコレクションを選べない。変異 M-4（Index にも効かせる）はここで赤になる。
    [Fact]
    public void Indexでは読み先の指定を無視する()
    {
        var router = Build(ruriEnabled: true);

        // 公開の取り込みで ruri を名乗っても、従来どおり優先度順（voyage）。
        router.Route(new EmbeddingRoutingRequest(SensitivityClass.Public, EmbeddingRoutePurpose.Index, Ruri))
            .EndpointName.Should().Be("voyage-managed");
        // 高機密の取り込みで voyage を名乗っても、ティア A のまま（越境は開かない）。
        router.Route(new EmbeddingRoutingRequest(SensitivityClass.Restricted, EmbeddingRoutePurpose.Index, Voyage))
            .EndpointName.Should().Be("selfhosted-ruri");
    }

    // T-R-06: 高機密の取り込みはセルフホストが無効なら、何を名乗っても拒否（fail-closed は動かない）。
    [Theory]
    [InlineData(SensitivityClass.Confidential)]
    [InlineData(SensitivityClass.Restricted)]
    public void 高機密の取り込みは名乗っても越境しない(SensitivityClass sensitivity)
    {
        var decision = Build(ruriEnabled: false)
            .Route(new EmbeddingRoutingRequest(sensitivity, EmbeddingRoutePurpose.Index, Voyage));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("fail-closed");
    }

    // T-R-07: `QueryProfile` とは**積**で効く。両方が同じ先を指せば選ばれ、食い違えば拒否する
    // （片方で他方を上書きしない —— 絞り込みは積み重なるだけで広げる経路を作らない）。
    [Fact]
    public void QueryProfileとは積で効く()
    {
        Build(ruriEnabled: true, queryProfile: "selfhosted-ruri").Route(Query(Ruri))
            .EndpointName.Should().Be("selfhosted-ruri");

        var conflicting = Build(ruriEnabled: true, queryProfile: "voyage-managed").Route(Query(Ruri));
        conflicting.Allowed.Should().BeFalse();
        conflicting.Reason.Should().Contain("voyage-managed").And.Contain(Ruri);
    }
}
