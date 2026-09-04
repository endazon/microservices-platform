using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Features.McpClients;

namespace McpServer.Tests.Features.McpClients;

// FR-16, UC-09, SC-12, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly）/ IADR-0371 決定 3 /
// IADR-0377: 手書きの詰め替えを生成マッパへ置き換えた際の**振る舞い同値**を固定する。
//
// 🔴 **生成物を信じるのではなく、写った値を見る。** Mapperly は名前が一致しないプロパティを
// 黙って落とすことがあり、**列が 1 つ抜けても型は通る**。9 プロパティを 1 つずつ見る。
[Trait("TestKind", "Unit")]
public class McpClientMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static McpClient Register(
        McpClientKind kind = McpClientKind.Interactive,
        EgressTier tier = EgressTier.StandardExternal,
        IReadOnlyDictionary<string, string>? attributes = null)
        => McpClient.Register("agent-1", "社内エージェント", kind, attributes, tier, Now);

    // 陽性: 全 9 プロパティが値を保ったまま写る。
    [Fact]
    public void ToView_CopiesEveryProperty()
    {
        var client = Register(
            McpClientKind.ServiceAccount, EgressTier.SelfHosted,
            new Dictionary<string, string> { ["department"] = "sales" });

        var view = McpClientMapper.ToView(client);

        view.Id.Should().Be(client.Id);
        view.ClientId.Should().Be("agent-1");
        view.DisplayName.Should().Be("社内エージェント");
        view.Kind.Should().Be("service-account");
        view.Enabled.Should().BeTrue();
        view.Attributes.Should().ContainKey("department").WhoseValue.Should().Be("sales");
        view.EgressTier.Should().Be("self-hosted");
        view.RegisteredAt.Should().Be(Now);
        view.UpdatedAt.Should().Be(Now);
    }

    // 🔴 **列挙の語彙はケバブケースである**（契約と画面の語彙。列挙名をそのまま出さない）。
    [Theory]
    [InlineData(McpClientKind.Interactive, "interactive")]
    [InlineData(McpClientKind.ServiceAccount, "service-account")]
    public void ToView_MapsKindToContractVocabulary(McpClientKind kind, string expected)
        => McpClientMapper.ToView(Register(kind)).Kind.Should().Be(expected);

    // ADR-0024 §4, 08_data-egress-policy: ティアの表示名。3 つとも見る。
    [Theory]
    [InlineData(EgressTier.SelfHosted, "self-hosted")]
    [InlineData(EgressTier.ProtectedExternal, "protected-external")]
    [InlineData(EgressTier.StandardExternal, "standard-external")]
    public void ToView_MapsEgressTierToDisplayName(EgressTier tier, string expected)
        => McpClientMapper.ToView(Register(tier: tier)).EgressTier.Should().Be(expected);

    // 🔴 **未知のティア値は最も低い保護水準へ倒す**（既定は安全側。移送前の `switch` の `_` と同じ）。
    // 保存済みの行が将来の値を持ち得るため、ここは例外にしない。
    [Fact]
    public void ToView_UnknownEgressTier_FallsBackToStandardExternal()
    {
        var client = Register(tier: (EgressTier)99);

        McpClientMapper.ToView(client).EgressTier.Should().Be("standard-external");
    }

    // 陰性: 属性なしで登録したクライアントは空の辞書を返す（null へ倒れない）。
    [Fact]
    public void ToView_WithoutAttributes_ReturnsEmptyDictionary()
    {
        var view = McpClientMapper.ToView(Register());

        view.Attributes.Should().NotBeNull();
        view.Attributes.Should().BeEmpty();
    }

    // 陰性 2: 無効化した状態が写り直る（写像が古い値を握らない）。
    [Fact]
    public void ToView_ReflectsDisabledState()
    {
        var client = Register();
        client.SetEnabled(false, Now.AddMinutes(5));

        var view = McpClientMapper.ToView(client);

        view.Enabled.Should().BeFalse();
        view.UpdatedAt.Should().Be(Now.AddMinutes(5));
    }
}
