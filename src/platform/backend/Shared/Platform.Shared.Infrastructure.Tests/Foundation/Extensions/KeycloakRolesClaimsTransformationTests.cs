using System.Security.Claims;
using AwesomeAssertions;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// FR-09, ADR-0004 / #901: KeycloakRolesClaimsTransformation の realm_access.roles → ClaimTypes.Role
// 展開を固定する。全サービスが `AddPlatformAuth` 経由でこの変換器に依存する
// （AuthExtensions.cs が `IClaimsTransformation` として登録する）ため、ここが壊れると
// `RequireRole("platform-admin")` 等の RBAC が実 Keycloak トークンに対して全滅する
// （実 Keycloak は realm_access.roles をネスト JSON クレームとしてしか渡さず、標準ハンドラは
// 自動展開しない）。**fail-closed（不正な入力ではロールを付与しない）であることを本試験の中心に置く**
// —— 逆に fail-open（不正入力でロールが付いてしまう）だと、細工したトークンで権限昇格し得る。
public class KeycloakRolesClaimsTransformationTests
{
    private static readonly KeycloakRolesClaimsTransformation Sut = new();

    private static ClaimsPrincipal Authenticated(params Claim[] extraClaims)
    {
        var identity = new ClaimsIdentity(extraClaims, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal Unauthenticated(params Claim[] extraClaims)
    {
        // authenticationType を渡さない ClaimsIdentity は IsAuthenticated=false になる。
        var identity = new ClaimsIdentity(extraClaims);
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task realm_accessのrolesがClaimTypes_Roleへ展開される()
    {
        var principal = Authenticated(
            new Claim("realm_access", """{"roles":["platform-admin","platform-operator"]}"""));

        var result = await Sut.TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Select(c => c.Value)
            .Should().BeEquivalentTo(["platform-admin", "platform-operator"]);
    }

    [Fact]
    public async Task 未認証のPrincipalはロールを付与しない_failclosed()
    {
        // AC: fail-closed。未認証（IsAuthenticated=false）はそもそも realm_access を信用しない。
        var principal = Unauthenticated(
            new Claim("realm_access", """{"roles":["platform-admin"]}"""));

        var result = await Sut.TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }

    [Fact]
    public async Task realm_accessクレームが無ければ何も付与しない()
    {
        var principal = Authenticated();

        var result = await Sut.TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"roles\": ")]
    [InlineData("null")]
    public async Task 不正なJSONは例外を投げずロールを付与しない_failclosed(string malformed)
    {
        // AC: 細工・破損したトークンでも変換器が落ちない（認証パイプライン全体を巻き込まない）
        // ことと、ロールが付かない（fail-closed）ことの両方を見る。
        var principal = Authenticated(new Claim("realm_access", malformed));

        var act = async () => await Sut.TransformAsync(principal);

        await act.Should().NotThrowAsync();
        var result = await Sut.TransformAsync(principal);
        result.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"roles":"platform-admin"}""")]
    [InlineData("""{"other":["platform-admin"]}""")]
    [InlineData("[1,2,3]")]
    public async Task roles配列を欠く形状はロールを付与しない(string shapeWithoutRolesArray)
    {
        var principal = Authenticated(new Claim("realm_access", shapeWithoutRolesArray));

        var result = await Sut.TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }

    [Fact]
    public async Task roles配列内の非文字列と空白は無視される()
    {
        var principal = Authenticated(
            new Claim("realm_access", """{"roles":["platform-admin", 1, null, "  ", "platform-operator"]}"""));

        var result = await Sut.TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Select(c => c.Value)
            .Should().BeEquivalentTo(["platform-admin", "platform-operator"]);
    }

    [Fact]
    public async Task 同じPrincipalへ複数回適用しても重複しない_冪等()
    {
        // 実運用では AuthenticateAsync の都度呼ばれ得る。重複付与は RequireRole の判定自体は
        // 壊さないが、クレーム集合が呼び出し回数に比例して膨らむのを防ぐ。
        var principal = Authenticated(
            new Claim("realm_access", """{"roles":["platform-admin"]}"""));

        await Sut.TransformAsync(principal);
        await Sut.TransformAsync(principal);
        var result = await Sut.TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Should().ContainSingle().Which.Value.Should().Be("platform-admin");
    }

    [Fact]
    public async Task 既存のRoleクレームは保持したまま追加する()
    {
        var principal = Authenticated(
            new Claim(ClaimTypes.Role, "already-present"),
            new Claim("realm_access", """{"roles":["platform-admin"]}"""));

        var result = await Sut.TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Select(c => c.Value)
            .Should().BeEquivalentTo(["already-present", "platform-admin"]);
    }
}
