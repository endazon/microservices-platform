using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Observability;

// FR-05, FR-09, SC-12, ADR-0036, ADR-0062, ADR-0085 決定 4, [[IADR-0420]] (#1233):
// **「ユニットの主体（無人主体）か」の判定規則。**
//
// 🔴 **陽性対照が本体である。** 「無人主体を無人と読む」だけなら、
// 「クライアント識別クレームを持つか」で書いても緑になる —— しかし `azp` は
// **人間のトークンにも必ず付く**ので、その実装は**全利用者を無人主体にする。**
// 落とせるのは「対話ログインした人間は無人ではない」を測る試験だけである。
[Trait("TestKind", "Unit")]
public class MachinePrincipalTests
{
    private const string KbWriter = "ai-stock-trading-kb-writer";

    private static ClaimsPrincipal Authenticated(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "Test"));

    // ★ 腕 A: Keycloak は client credentials の主体へ
    // `preferred_username = service-account-<clientId>` を発行する。
    [Fact]
    public void サービスアカウントのpreferred_usernameは無人主体と判定する()
        => MachinePrincipal.IsMachine(Authenticated(
                new Claim("preferred_username", $"service-account-{KbWriter}"),
                new Claim("azp", KbWriter)))
            .Should().BeTrue();

    // ★ 腕 B: `profile` スコープを持たない機械クライアントは利用者名を発行しない。
    [Fact]
    public void 利用者名を持たずクライアント識別だけを持つ主体は無人主体と判定する()
        => MachinePrincipal.IsMachine(Authenticated(new Claim("azp", KbWriter)))
            .Should().BeTrue();

    // 🔴 ★ **陽性対照 1**: 対話ログインした人間は無人主体ではない。
    // **`azp` を持っていてもである** —— SPA の clientId が必ず入る。
    // 「クライアント識別クレームを持つか」だけで書いた実装はここで落ちる。
    [Fact]
    public void 対話ログインの利用者はazpを持っていても無人主体と判定しない()
        => MachinePrincipal.IsMachine(Authenticated(
                new Claim("preferred_username", "hanako"),
                new Claim("azp", "platform-spa")))
            .Should().BeFalse();

    // 🔴 ★ **陽性対照 2**: 名前が `service-account-` を**含む**だけの利用者は無人主体ではない
    // （判定は接頭辞であって部分一致ではない）。
    [Fact]
    public void 名前にservice_accountを含むだけの利用者は無人主体と判定しない()
        => MachinePrincipal.IsMachine(Authenticated(
                new Claim("preferred_username", "not-a-service-account-user")))
            .Should().BeFalse();

    // ★ 未認証は無人主体ではない（主体が決まらないものを機械と読まない）。
    [Fact]
    public void 未認証は無人主体と判定しない()
        => MachinePrincipal.IsMachine(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeFalse();

    // ★ null も同じ（呼び出し側で分岐させない）。
    [Fact]
    public void nullは無人主体と判定しない()
        => MachinePrincipal.IsMachine(null).Should().BeFalse();

    // ★ `Identity.Name` 経由でも読める（`AddPlatformAuth` は NameClaimType を
    // `preferred_username` に設定するが、テスト用ハンドラは `ClaimTypes.Name` を使う）。
    [Fact]
    public void ClaimTypesNameのサービスアカウント名でも無人主体と判定する()
        => MachinePrincipal.IsMachine(Authenticated(
                new Claim(ClaimTypes.Name, $"service-account-{KbWriter}"),
                new Claim("azp", KbWriter)))
            .Should().BeTrue();

    // ── NFR-09, [[IADR-0606]]（GHSA-94cm-55g9-2967）: 腕 A は利用者名とクライアント識別のクレームの一致で判定する ──

    // 🔴 ★ 利用者名の接頭辞だけでは無人と読まない。発行元のクライアント（`azp`）が別なら人である。
    [Theory]
    [InlineData("bff")]
    [InlineData("wiki-js")]
    [InlineData("ai-stock-trading-kb-writer-x")]
    public void 接頭辞を持つ利用者名でも発行元のクライアントと一致しなければ無人主体と判定しない(string azp)
        => MachinePrincipal.IsMachine(Authenticated(
                new Claim("preferred_username", $"service-account-{KbWriter}"),
                new Claim("azp", azp)))
            .Should().BeFalse();

    // 🔴 ★ クライアント識別のクレームが無ければ、利用者名がどうであれ無人と読まない。
    [Fact]
    public void クライアント識別のクレームが無ければ接頭辞を持つ利用者名でも無人主体と判定しない()
        => MachinePrincipal.IsMachine(Authenticated(
                new Claim("preferred_username", $"service-account-{KbWriter}")))
            .Should().BeFalse();

    // ★ `client_id` クレーム（Keycloak の `service_account` スコープの形）でも一致すれば無人。
    [Fact]
    public void client_idクレームと利用者名が一致すれば無人主体と判定する()
        => MachinePrincipal.IsMachine(Authenticated(
                new Claim("preferred_username", $"service-account-{KbWriter}"),
                new Claim("client_id", KbWriter)))
            .Should().BeTrue();

    // ★ Keycloak は利用者名を小文字へ正規化する。クライアント ID に大文字があっても一致と読む。
    [Fact]
    public void 利用者名とクライアント識別子の大小の違いは一致と読む()
        => MachinePrincipal.IsMachine(Authenticated(
                new Claim("preferred_username", "service-account-mcp-client-abc"),
                new Claim("azp", "MCP-Client-ABC")))
            .Should().BeTrue();

    // 🔴 ★ 接頭辞だけ・空のクライアント名の形は一致と読まない。
    [Fact]
    public void 接頭辞だけの利用者名は空でないクライアント識別子と一致しない()
        => MachinePrincipal.IsMachine(Authenticated(
                new Claim("preferred_username", "service-account-"),
                new Claim("azp", KbWriter)))
            .Should().BeFalse();

    // ★ 陽性対照（正規の無人主体の母集合）: realm の宣言のサービスアカウントは、Keycloak が発行する形
    // （利用者名 `service-account-<clientId>`・`azp=<clientId>`／`profile` を持たなければ利用者名なし）の
    // どちらでも無人と判定する。母集合は realm の宣言から引く（手で列挙しない）。
    [Fact]
    public void realmの宣言のサービスアカウントはすべて無人主体と判定する()
    {
        var clients = RealmServiceAccountClients();
        clients.Should().HaveCountGreaterThan(10, "realm の宣言を読めていること（0 件で緑にしない）");
        clients.Should().Contain(["bff", "document-service", KbWriter, "ai-stock-trading-kb-reader"]);

        foreach (var (clientId, username) in clients.Select(c => (c, RealmServiceAccountUsername(c))))
        {
            MachinePrincipal.IsMachine(Authenticated(
                    new Claim("preferred_username", username), new Claim("azp", clientId)))
                .Should().BeTrue($"{clientId}（利用者名あり）は無人主体である");
            MachinePrincipal.IsMachine(Authenticated(new Claim("azp", clientId)))
                .Should().BeTrue($"{clientId}（利用者名なし）は無人主体である");
        }
    }

    // ★ 陰性対照（人の利用者の母集合）: realm の宣言の人の利用者は、どのクライアントの `azp` でも無人と判定しない。
    [Fact]
    public void realmの宣言の人の利用者はどのクライアントでも無人主体と判定しない()
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(RealmPath()));
        var humans = realm.RootElement.GetProperty("users").EnumerateArray()
            .Where(u => !u.TryGetProperty("serviceAccountClientId", out var sa) || string.IsNullOrEmpty(sa.GetString()))
            .Select(u => u.GetProperty("username").GetString()!)
            .ToArray();
        humans.Should().NotBeEmpty();
        var allClients = realm.RootElement.GetProperty("clients").EnumerateArray()
            .Select(c => c.GetProperty("clientId").GetString()!).ToArray();

        foreach (var human in humans)
            foreach (var azp in allClients)
                MachinePrincipal.IsMachine(Authenticated(
                        new Claim("preferred_username", human), new Claim("azp", azp)))
                    .Should().BeFalse($"人の利用者 {human}（azp={azp}）は無人主体ではない");
    }

    private static string[] RealmServiceAccountClients()
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(RealmPath()));
        return realm.RootElement.GetProperty("clients").EnumerateArray()
            .Where(c => c.TryGetProperty("serviceAccountsEnabled", out var sa) && sa.ValueKind == JsonValueKind.True)
            .Select(c => c.GetProperty("clientId").GetString()!)
            .ToArray();
    }

    private static string RealmServiceAccountUsername(string clientId)
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(RealmPath()));
        return realm.RootElement.GetProperty("users").EnumerateArray()
            .Where(u => u.TryGetProperty("serviceAccountClientId", out var sa) && sa.GetString() == clientId)
            .Select(u => u.GetProperty("username").GetString())
            .FirstOrDefault()
            // 宣言に無いサービスアカウントの利用者は、取り込みで Keycloak が `service-account-<clientId 小文字>` として作る。
            ?? "service-account-" + clientId.ToLowerInvariant();
    }

    // 解決できなければ止める（fail-closed）。
    private static string RealmPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "deploy", "keycloak", "microservices-platform-realm.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("deploy/keycloak/microservices-platform-realm.json を解決できなかった。");
    }

    // ★ 属性に載せる値は `azp` の生値である。
    [Fact]
    public void クライアント識別子はazpの生値を返す()
        => MachinePrincipal.ClientIdOf(Authenticated(
                new Claim("preferred_username", $"service-account-{KbWriter}"),
                new Claim("azp", KbWriter)))
            .Should().Be(KbWriter);

    // ★ `azp` が無ければ `service-account-<clientId>` から復元する
    // （realm の設定次第で `azp` が落ちても、属性が `unknown` へ潰れない）。
    [Fact]
    public void azpが無ければサービスアカウント名からクライアント識別子を復元する()
        => MachinePrincipal.ClientIdOf(Authenticated(
                new Claim("preferred_username", $"service-account-{KbWriter}")))
            .Should().Be(KbWriter);

    // ★ どちらも無ければ null（呼び出し側が既定値へ倒す）。
    [Fact]
    public void クライアント識別子が無ければnullを返す()
        => MachinePrincipal.ClientIdOf(Authenticated(new Claim("preferred_username", "hanako")))
            .Should().BeNull();
}
