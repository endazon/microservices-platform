using System.Net;
using AwesomeAssertions;
using Platform.Bff.Foundation.Endpoints;

namespace Platform.Bff.Tests;

// 🔴 FR-05, FR-16, SC-12, SC-16, ADR-0062 決定 4: **身元の口（`/bff/auth/me`）へ
// `clearance` / `department` を足さない。**
//
// ■ なぜ不在を試験するのか
//   属性の正は認可サービスであり、画面へ配ると「画面が持つ値」と「判定に使う値」の 2 つができる。
//   さらに**画面が属性を持つと「画面で判定できる」ように見えてしまう** —— ADR-0062 が
//   「信頼できない検証は無いより悪い」として退けた形を、**契約の側から誘発する**ことになる。
//   規約で禁じるだけでは、次に SC-12 を触る人が「即時フィードバックのために」足してしまう。
//   **不在を検査で固定する。**
//
// ■ 🔴 陽性対照を対で置く
//   「含まない」だけのテストは、端点が常に 401 を返す実装でも、DTO が空になっても緑になる。
//   同じ手段で**在るはず**のもの（`roles`）が取れることを確かめる。
public class BffIdentityContractTests
{
    private static readonly string[] Forbidden = ["clearance", "department"];

    // 契約型そのものに口が無いこと（反射）。**型で持てなくする**のが一次の防波堤である。
    [Fact]
    public void BffIdentityDto_does_not_expose_abac_attributes()
    {
        var properties = typeof(BffIdentityDto).GetProperties().Select(p => p.Name).ToList();

        // ★ 陽性対照: 反射は当たっている（在るものは見えている）。
        properties.Should().Contain("Roles");

        foreach (var name in Forbidden)
        {
            properties.Should().NotContain(
                p => p.Contains(name, StringComparison.OrdinalIgnoreCase),
                $"ADR-0062 決定 4 は身元の口へ {name} を足さないと定めている");
        }
    }

    // 実応答にも現れないこと。**セッションが当該クレームを持っていても返さない** ——
    // 「たまたま持っていないから出ない」では、クレームが増えた日に静かに漏れる。
    [Fact]
    public async Task Me_never_returns_abac_attributes_even_when_the_session_carries_them()
    {
        await using var host = await SessionTestHost.StartAsync();
        var cookie = await host.SignInAsync(
            "alice", sid: "sess-abac", roles: "platform-admin",
            attrs: "clearance=restricted,department=hr");

        var resp = await host.Client.SendAsync(
            host.Request(HttpMethod.Get, "/bff/auth/me", cookie), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // ★ 陽性対照: 返るべきものは返っている（＝ 401 や空応答で緑になっていない）。
        body.Should().Contain("platform-admin");

        body.Should().NotContain("clearance");
        body.Should().NotContain("department");
        // 値そのものも出ない（キー名を変えて運ぶ抜け道を塞ぐ）。
        body.Should().NotContain("restricted");
        body.Should().NotContain("\"hr\"");
    }
}
