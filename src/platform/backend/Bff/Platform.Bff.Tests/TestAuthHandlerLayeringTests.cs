using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Bff.Foundation.Session;

namespace Platform.Bff.Tests;

// NFR, ADR-0032, IADR-0251 決定 9, #439 第 3 段(3b): **試験の器が振り分けのどちら側に挿さるか**を測る。
//
// 3b ① で既定を振り分けスキーム（`BffSmart`）にした。`BffTestFactory` は
// `AddAuthentication(TestAuthHandler.SchemeName)` を呼ぶので、**既定がさらに上書きされる**。
// つまり器は**振り分けの前に挿さり、振り分けを迂回する**——はずである。**それを実測する。**
//
// 🔴 **これを測る理由**: 迂回しているなら、既存テストの緑は
// 「振り分けが正しい」ことも「セッション経路が動く」ことも示さない。
// **器の射程を明示しておかないと、次に読む人が緑を過大に読む。**
public class TestAuthHandlerLayeringTests(BffTestFactory factory) : IClassFixture<BffTestFactory>
{
    // ★ 実測: 試験の器では既定が `Test` であり、**振り分けスキームではない**。
    [Fact]
    public void Test_factory_overrides_the_default_scheme_and_bypasses_the_forwarder()
    {
        using var scope = factory.Services.CreateScope();
        var auth = scope.ServiceProvider
            .GetRequiredService<IOptions<AuthenticationOptions>>().Value;

        auth.DefaultScheme.Should().Be(
            TestAuthHandler.SchemeName,
            "器が既定を奪うので、既存テストは振り分けを通らない");
        auth.DefaultScheme.Should().NotBe(
            BffSessionExtensions.SmartScheme,
            "ここが SmartScheme になっていたら、器の前提が変わったので本ファイルを見直すこと");
    }

    // ★ ただし**振り分け先の 2 スキームは登録されたまま**である
    // （器は既定を奪うだけで、本番の配線を消してはいない）。
    [Fact]
    public async Task Both_real_schemes_remain_registered_under_the_test_factory()
    {
        using var scope = factory.Services.CreateScope();
        var schemes = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();

        (await schemes.GetSchemeAsync(BffSessionExtensions.SessionScheme)).Should().NotBeNull();
        (await schemes.GetSchemeAsync(BffSessionExtensions.SmartScheme)).Should().NotBeNull();
    }
}
