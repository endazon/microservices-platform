using AuthorizationService.Domain.Ports;
using AuthorizationService.Features.Users;
using AwesomeAssertions;

namespace AuthorizationService.Tests.Features.Users;

// SC-17, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly）/ IADR-0371 決定 3 / IADR-0393:
// 手書きの詰め替えを生成マッパへ置き換えた際の**振る舞い同値**を固定する。
//
// 🔴 **生成物を信じるのではなく、写った値を見る。** Mapperly は名前が一致しないプロパティを
// 黙って落とすことがあり、**列が 1 つ抜けても型は通る**。6 プロパティを 1 つずつ見る。
[Trait("TestKind", "Unit")]
public class PlatformUserMapperTests
{
    private static IdentityUser User(
        IReadOnlyList<string>? roles = null,
        IReadOnlyDictionary<string, string>? attributes = null)
        => new("u-1", "alice", "アリス", true, roles ?? [], attributes ?? new Dictionary<string, string>());

    // 陽性: 全 6 プロパティが値を保ったまま写る。
    [Fact]
    public void ToDto_CopiesEveryProperty()
    {
        var user = User(
            ["platform-admin", "viewer"],
            new Dictionary<string, string> { ["department"] = "sales", ["clearance"] = "internal" });

        var dto = PlatformUserMapper.ToDto(user);

        dto.Id.Should().Be("u-1");
        dto.Username.Should().Be("alice");
        dto.DisplayName.Should().Be("アリス");
        dto.Enabled.Should().BeTrue();
        dto.Roles.Should().Equal("platform-admin", "viewer");
        dto.Attributes.Should().HaveCount(2);
        dto.Attributes["department"].Should().Be("sales");
        dto.Attributes["clearance"].Should().Be("internal");
    }

    // 陰性: 無効化された利用者は Enabled=false のまま写る。
    // **`Enabled` が落ちると「無効なのに有効に見える」** —— 画面が最も誤りやすい列である。
    [Fact]
    public void ToDto_KeepsDisabledState()
    {
        var user = new IdentityUser("u-2", "bob", "ボブ", false, [], new Dictionary<string, string>());

        PlatformUserMapper.ToDto(user).Enabled.Should().BeFalse();
    }

    // 陰性 2: ロール・属性が空でも空のコレクションで返る（null へ倒れない）。
    [Fact]
    public void ToDto_WithoutRolesOrAttributes_ReturnsEmptyCollections()
    {
        var dto = PlatformUserMapper.ToDto(User());

        dto.Roles.Should().NotBeNull().And.BeEmpty();
        dto.Attributes.Should().NotBeNull().And.BeEmpty();
    }

    // 🔴 **コレクションは複製である**（移送前の `[.. user.Roles]` / `new Dictionary<…>(…)` と同じ）。
    // 参照を共有すると、応答 DTO を書き換えた誰かが**認可基盤から読んだ身元そのもの**を汚す。
    [Fact]
    public void ToDto_CopiesCollections_RatherThanSharingReferences()
    {
        var roles = new List<string> { "viewer" };
        var attributes = new Dictionary<string, string> { ["department"] = "sales" };
        var dto = PlatformUserMapper.ToDto(User(roles, attributes));

        dto.Roles.Add("platform-admin");
        dto.Attributes["department"] = "hr";

        // 🔴 `Equal(params T[])` に理由文字列を渡すと**要素として扱われる**（実測で赤になった）。
        // 理由を添えたい比較は `BeEquivalentTo` の側で書く。
        roles.Should().BeEquivalentTo(["viewer"], "写像の結果を書き換えても元のロールは動かない");
        attributes["department"].Should().Be("sales", "写像の結果を書き換えても元の属性は動かない");
    }
}
