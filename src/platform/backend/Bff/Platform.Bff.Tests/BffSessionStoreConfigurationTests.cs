using AwesomeAssertions;
using Platform.Bff.Foundation.Session;
using StackExchange.Redis;
using Xunit;

namespace Platform.Bff.Tests;

// NFR-18, ADR-0131 決定 4 の 2, [[IADR-0522]] (#1839 / #1860 監査指摘 1・5):
// **`SessionStoreConfiguration()` が `RedisPassword` を構成へ載せること（無ければ載せないこと）を、Docker 無しの単体で固定する。**
// 実イメージでの認証は BffSessionStoreValkeyTests（Integration）が測るが、PR の ci.yml はそれを外すので、
// 「パスワードが構成へ伝わらない」退行は本クラスが無いと PR で捕まらない。
public sealed class BffSessionStoreConfigurationTests
{
    // 実在の鍵に見えない形を実行時に組む（gitleaks）。
    private static string Dummy(params string[] parts) => string.Join("-", ["dummy", .. parts]);

    [Fact]
    public void パスワードがあれば構成に載り_接続先は保たれる()
    {
        var password = Dummy("x");
        var options = new BffSessionOptions { RedisConnectionString = "valkey:6379", RedisPassword = password };

        var configuration = options.SessionStoreConfiguration();

        configuration.Password.Should().Be(password);
        configuration.EndPoints.Select(e => e.ToString()).Should().ContainSingle().Which.Should().Contain("valkey").And.Contain("6379");
    }

    [Fact]
    public void パスワードが空なら構成にパスワードを載せない()
    {
        var options = new BffSessionOptions { RedisConnectionString = "valkey:6379", RedisPassword = string.Empty };

        options.SessionStoreConfiguration().Password.Should().BeNullOrEmpty();
    }

    // 🔴 構成文字列（`k=v,k=v`）の区切りに当たる `,` と `=` を含むパスワードでも、文字列の連結を経ずに
    // そのまま構成へ載ること（連結すると接続先・他の設定として誤読される）。
    [Theory]
    [InlineData(",")]
    [InlineData("=")]
    [InlineData(",abortConnect=false")]
    public void 区切り文字を含むパスワードも欠けずに構成へ載る(string separator)
    {
        var password = Dummy("a") + separator + Dummy("b");
        var options = new BffSessionOptions { RedisConnectionString = "valkey:6379", RedisPassword = password };

        var configuration = options.SessionStoreConfiguration();

        configuration.Password.Should().Be(password);
        configuration.EndPoints.Should().ContainSingle();
    }

    // 呼ぶたびに別の実体を返す（利用者ごとに設定を足しても他の利用者へ漏れない）。
    [Fact]
    public void 呼ぶたびに別の構成を返す()
    {
        var options = new BffSessionOptions { RedisConnectionString = "valkey:6379", RedisPassword = Dummy("x") };

        options.SessionStoreConfiguration().Should().NotBeSameAs(options.SessionStoreConfiguration());
    }
}
