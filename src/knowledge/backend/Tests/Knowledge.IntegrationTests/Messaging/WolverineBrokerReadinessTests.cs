using System.Net;
using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Wolverine;
using Wolverine.RabbitMQ;

namespace Knowledge.IntegrationTests.Messaging;

// ADR-0027 / NFR（#441 W4）: Wolverine へ移した発行元のブローカ readiness を実ブローカで固定する。
//
// 🔴 **これが W4 の完了条件である。** 単体試験は「登録されたか」までしか見られず、
// **「ブローカが落ちたときに本当に 503 になるか」は実ブローカでしか測れない。**
// 200 を返し続ける readiness は、無いのと同じであるうえに「在るように見える」ぶん悪い。
//
// 🔴 **起動時のブローカ障害は本試験の対象外である。** 実測したところ、到達不能なブローカに対して
// Wolverine ホストは **20 回再試行して約 135 秒後に BrokerInitializationException で起動に失敗**する。
// ホストが立たない以上 /health/ready は存在せず、**試験対象が無い**（pod は crash loop になり、
// readiness ではなく起動そのもので止まる＝それ自体は安全側）。
// **readiness が守れるのは「起動後に到達できなくなる」場合だけ**である。
[Trait("Category", "Integration")]
public sealed class WolverineBrokerReadinessTests(RabbitMqFixture rabbit) : IClassFixture<RabbitMqFixture>
{
    // 検知までの待ち上限。実測では遮断から約 3 秒で Unhealthy へ落ちた。
    private static readonly TimeSpan DetectTimeout = TimeSpan.FromSeconds(60);

    private static async Task<WebApplication> StartAsync(Uri amqp)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.UseRabbitMq(amqp);
        });

        // 🔴 opt-in。共通ヘルパ単独では 1 件も登録されない（W4 決定）。
        builder.Services.AddPlatformHealthChecks().AddPlatformWolverineBroker();

        var app = builder.Build();
        app.MapPlatformHealthChecks();
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpStatusCode> GetAsync(WebApplication app, string path) =>
        (await app.GetTestClient().GetAsync(path)).StatusCode;

    [BrokerFact]
    public async Task ブローカ健全時はreadyが200を返す_陽性対照()
    {
        // 🔴 **これが無いと次の試験は無意味になる。**「常に 503 を返す実装」でも
        // 503 の試験だけなら緑になるからである。**検出器が生きていることを先に示す。**
        var conn = rabbit.ConnectionString;
        conn.Should().NotBeNull();

        await using var gate = BrokerTcpGate.Start(conn!);
        await using var app = await StartAsync(BrokerTcpGate.RewriteToLocal(conn!, gate.LocalPort));

        (await GetAsync(app, "/health/ready")).Should().Be(
            HttpStatusCode.OK, "ブローカへ到達できているなら ready は 200 である");
        (await GetAsync(app, "/health/live")).Should().Be(
            HttpStatusCode.OK, "live 側は Predicate = _ => false のため常に 200 である");
    }

    [BrokerFact]
    public async Task 起動後にブローカへ到達できなくなるとreadyが503になりliveは200のまま()
    {
        // 🔴 W4 の完了条件そのもの。
        // ブローカ本体は止めない —— **自分が作った中継だけを落とす**（共有資源に触れない）。
        var conn = rabbit.ConnectionString;
        conn.Should().NotBeNull();

        await using var gate = BrokerTcpGate.Start(conn!);
        await using var app = await StartAsync(BrokerTcpGate.RewriteToLocal(conn!, gate.LocalPort));

        (await GetAsync(app, "/health/ready")).Should().Be(
            HttpStatusCode.OK, "遮断前は 200 でなければ、以後の 503 が遮断由来だと言えない");

        gate.Sever();

        var ready = await WaitForAsync(app, "/health/ready", HttpStatusCode.ServiceUnavailable);
        ready.Should().Be(
            HttpStatusCode.ServiceUnavailable,
            "ブローカへ到達できないのに ready が 200 を返すなら、readiness は無いのと同じである");

        (await GetAsync(app, "/health/live")).Should().Be(
            HttpStatusCode.OK,
            "ブローカ障害で live まで落ちると、依存障害で全 pod が再起動ループへ入る");
    }

    // 期待の状態になるまで待つ。ならなければ**最後の実測値を返して落とす**
    // （待って諦めて緑、にはしない）。
    private static async Task<HttpStatusCode> WaitForAsync(
        WebApplication app, string path, HttpStatusCode expected)
    {
        var deadline = DateTimeOffset.UtcNow + DetectTimeout;
        HttpStatusCode last;
        do
        {
            last = await GetAsync(app, path);
            if (last == expected) return last;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        while (DateTimeOffset.UtcNow < deadline);

        return last;
    }
}
