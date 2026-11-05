using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Platform.Bff.Tests;

// NFR-18, IADR-0686 (#2432・AI レビュー): 利用者ごとの上限の設定は**起動時に**検証し、不正なら起動を止める
// （最初の要求で 500 にしない。LLM ゲートウェイの段の切り替えと同じ方針）。
public sealed class BffUserRateLimitStartupTests : IDisposable
{
    private readonly BffTestFactory _base = new();

    public void Dispose() => _base.Dispose();

    private Exception? StartWith(string key, string value)
    {
        using var app = _base.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        try
        {
            using var _ = app.CreateClient();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Theory]
    [InlineData("Bff:UserRateLimits:Mode", "Enfroce")]
    [InlineData("Bff:UserRateLimits:Groups:analysis", "0")]
    [InlineData("Bff:UserRateLimits:Groups:analysis", "-3")]
    [InlineData("Bff:UserRateLimits:Groups:analysis", "abc")]
    [InlineData("Bff:UserRateLimits:Groups:analysys", "5")]
    public void 不正な設定では起動を止める(string key, string value)
        => StartWith(key, value).Should().NotBeNull($"{key}={value} は起動の時点で止める");

    [Theory]
    [InlineData("Bff:UserRateLimits:Mode", "Enforce")]
    [InlineData("Bff:UserRateLimits:Groups:search", "5")]
    public void 正しい設定では起動する(string key, string value)
        => StartWith(key, value).Should().BeNull();
}
