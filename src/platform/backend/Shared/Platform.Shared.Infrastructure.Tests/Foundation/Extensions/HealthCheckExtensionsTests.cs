using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// NFR / #901: MapPlatformHealthChecks の述語が実際に効いていることを固定する。
// 本番 11 箇所が使い、/health/live の唯一の消費者は k8s の probe である。
//   - live 側が `_ => false` から `_ => true` へ退行すれば、依存障害で**全 pod が再起動ループ**する
//   - ready 側が `_ => false` へ退行すれば、**未準備 pod へ常時トラフィック**が流れる
// 🔴 **どちらも HTTP は 200 を返し続け、ビルドも通る。** 最も静かで最も広い退行である。
//
// 🔴 **「適用前の既定値」を先に assert する**（IADR-0233 / WolverineExtensionsTests の作法）。
// /health/live は **ヘルスチェックを 1 つも登録しなければ、述語が何であっても 200** になる。
// 適用後だけを見ると **ヘルパが何もしなくても緑**になり、変異が当たらない試験になる。
// そこで「失敗するチェックを先に登録し、live では無視され ready では 503 を返す」ことを**対で**見る。
//
// 🔴 **リフレクションではなく実 HTTP 応答で観測する。** リフレクションのみの試験は
// 被対象の実行行を 1 行も通らず、行被覆に寄与しない（#901 の前提）。
public class HealthCheckExtensionsTests
{
    /// <summary>プロセス内に WebApplication を建て、MapPlatformHealthChecks を適用した TestServer を返す。</summary>
    private static async Task<WebApplication> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapPlatformHealthChecks();
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpStatusCode> GetAsync(WebApplication app, string path)
    {
        var res = await app.GetTestClient().GetAsync(path);
        return res.StatusCode;
    }

    // 試験 2（対照）: 適用前の既定値。チェックが 0 件なら述語に関係なく両方 200 になる。
    // **この試験だけでは何も証明されない。** 試験 1 と対で読むためにある。
    [Fact]
    public async Task 適用前の既定値_チェックが0件なら述語に関係なく両方200を返す()
    {
        await using var app = await StartAsync(s => s.AddPlatformHealthChecks());

        (await GetAsync(app, "/health/live")).Should().Be(HttpStatusCode.OK);
        (await GetAsync(app, "/health/ready")).Should().Be(
            HttpStatusCode.OK,
            "チェックが 0 件なら ready 側も落ちようがない。試験 1 と対で読むこと");
    }

    // 試験 1（本命）: 失敗する ready タグ付きチェックを登録する。
    // live は無視して 200、ready は拾って 503 —— この**対**で初めて述語が効いていると言える。
    [Fact]
    public async Task 失敗するreadyチェックがあるとliveは200のままreadyだけが503になる()
    {
        await using var app = await StartAsync(s => s
            .AddPlatformHealthChecks()
            .AddCheck("dependency", () => HealthCheckResult.Unhealthy("down"), tags: ["ready"]));

        (await GetAsync(app, "/health/live")).Should().Be(
            HttpStatusCode.OK,
            "live の述語は _ => false であり、依存の失敗を無視しなければならない（さもなくば全 pod が再起動ループする）");
        (await GetAsync(app, "/health/ready")).Should().Be(
            HttpStatusCode.ServiceUnavailable,
            "ready の述語は ready タグを拾わなければならない（さもなくば未準備 pod へトラフィックが流れる）");
    }

    // 試験 3: ready タグの**無い**失敗チェックは ready 側でも拾わない（タグで選別していることの固定）。
    [Fact]
    public async Task readyタグの無い失敗チェックはready側でも拾われない()
    {
        await using var app = await StartAsync(s => s
            .AddPlatformHealthChecks()
            .AddCheck("untagged", () => HealthCheckResult.Unhealthy("down")));

        (await GetAsync(app, "/health/live")).Should().Be(HttpStatusCode.OK);
        (await GetAsync(app, "/health/ready")).Should().Be(
            HttpStatusCode.OK,
            "述語が Tags.Contains(\"ready\") ではなく _ => true へ退行していれば、ここで 503 になる");
    }

    // 試験 4: 登録される経路は 2 本だけである（経路名の退行を、述語の退行とは別のテストで捕まえる）。
    [Fact]
    public async Task 登録される経路はhealthliveとhealthreadyの2本だけである()
    {
        await using var app = await StartAsync(s => s.AddPlatformHealthChecks());

        (await GetAsync(app, "/health/live")).Should().Be(HttpStatusCode.OK);
        (await GetAsync(app, "/health/ready")).Should().Be(HttpStatusCode.OK);
        (await GetAsync(app, "/health")).Should().Be(HttpStatusCode.NotFound);
        (await GetAsync(app, "/health/readyz")).Should().Be(
            HttpStatusCode.NotFound,
            "経路名が変わったことは、述語の試験ではなくこの試験が捕まえる");
    }

    // 試験 5: AddPlatformHealthChecks は IHealthChecksBuilder を返し、HealthCheckService を登録する。
    [Fact]
    public void AddPlatformHealthChecksはビルダを返しヘルスチェックサービスを登録する()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var returned = services.AddPlatformHealthChecks();

        returned.Should().NotBeNull();
        returned.Services.Should().BeSameAs(services, "呼び出し側が続けてチェックを登録できること");
        services.BuildServiceProvider()
            .GetService<HealthCheckService>()
            .Should().NotBeNull();
    }
}
