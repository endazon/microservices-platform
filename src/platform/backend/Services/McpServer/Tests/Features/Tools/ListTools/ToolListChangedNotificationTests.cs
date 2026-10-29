using System.Text;
using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Features.Tools.ListTools;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using McpServer.Infrastructure.Authentication;
using McpServer.Tests.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using SdkMcpClient = ModelContextProtocol.Client.McpClient;
using ModelContextProtocol.Protocol;

namespace McpServer.Tests.Features.Tools.ListTools;

// FR-16, ADR-0024 §2, [[IADR-0651]] (#2142): 公開ツールの集合が変わったとき、接続中のクライアントへ `notifications/tools/list_changed` が届く。
//
// 実物の MCP SDK のクライアントで `/mcp` に繋ぎ（audience を検証するスキームを通る本物のトークン）、カタログの版を進める。
// 陰性の対照: 内容の変わらない突合（版が進まない）では届かない。
[Trait("TestKind", "Integration")]
public class ToolListChangedNotificationTests(McpAudienceAuthenticationTests.Factory factory)
    : IClassFixture<McpAudienceAuthenticationTests.Factory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Token() =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = McpAudienceAuthenticationTests.Factory.Issuer,
            Audience = McpAudienceAuthentication.Audience,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["azp"] = "human-x", ["preferred_username"] = "poc-user" },
            SigningCredentials = new SigningCredentials(McpAudienceAuthenticationTests.Factory.Key, SecurityAlgorithms.HmacSha256),
        });

    private static McpToolDeclaration Decl(string name) => new(name, "説明", """{"type":"object"}""", "read", "internal");

    // 受付（`notifications/subscriptions/acknowledged`）の受信の数も返す（listen が開いたことを時間ではなく受付で待つ）。
    private static async Task<(SdkMcpClient Client, Func<int> Received, Func<int> Acknowledged)> ConnectAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, string? protocolVersion)
    {
        var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token());
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http, ownsHttpClient: true);
        var received = 0;
        var acknowledged = 0;
        var client = await SdkMcpClient.CreateAsync(transport, new McpClientOptions
        {
            ProtocolVersion = protocolVersion,
            Handlers = new McpClientHandlers
            {
                NotificationHandlers =
                [
                    new(NotificationMethods.ToolListChangedNotification, (_, _) =>
                    {
                        Interlocked.Increment(ref received);
                        return ValueTask.CompletedTask;
                    }),
                    new(NotificationMethods.SubscriptionsAcknowledgedNotification, (_, _) =>
                    {
                        Interlocked.Increment(ref acknowledged);
                        return ValueTask.CompletedTask;
                    }),
                ],
            },
        }, cancellationToken: Ct);
        return (client, () => Volatile.Read(ref received), () => Volatile.Read(ref acknowledged));
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50, Ct);
        }
        return condition();
    }

    // 開いた `subscriptions/listen` の流れを返す（2026-07-28 の改訂。無状態の Streamable HTTP で通知が届く唯一の道）。
    // 🔴 ［#2315 のレビュー］流れが開いたことは**受付の受信**で待つ（時間で待つと、混んだ CI で受付より先に版を進めて取りこぼす）。
    private static async Task<(CancellationTokenSource Cts, Task<JsonRpcResponse> Listen)> StartListenAsync(
        SdkMcpClient client, Func<int> acknowledged, bool toolsListChanged = true)
    {
        var before = acknowledged();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listen = client.SendRequestAsync(new JsonRpcRequest
        {
            Method = RequestMethods.SubscriptionsListen,
            Params = System.Text.Json.JsonSerializer.SerializeToNode(
                new SubscriptionsListenRequestParams { Notifications = new() { ToolsListChanged = toolsListChanged } },
                ModelContextProtocol.McpJsonUtilities.DefaultOptions),
        }, cts.Token);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (acknowledged() == before && !listen.IsFaulted && DateTime.UtcNow < deadline)
            await Task.Delay(20, Ct);
        if (listen.IsFaulted) await listen;
        acknowledged().Should().BeGreaterThan(before, "listen の受付が届く");
        return (cts, listen);
    }

    private void Publish(string name)
    {
        var catalog = factory.Services.GetRequiredService<ToolCatalog>();
        catalog.Refresh(new ToolPublicationConfig("v1", [new ToolPublicationEntry(name, "retrieval")]),
            [new ServiceToolDeclarations("retrieval", [Decl(name)])]);
    }

    [Fact]
    public async Task 公開ツールの集合が変わるとlistenしているクライアントへlist_changedが届く()
    {
        var (client, received, acknowledged) = await ConnectAsync(factory, protocolVersion: null);
        await using var _ = client;
        client.ServerCapabilities.Tools?.ListChanged.Should().BeTrue("list_changed を送ることを申告する");
        var (listen, _) = await StartListenAsync(client, acknowledged);
        using var _listen = listen;

        Publish($"retrieval.search.{Guid.NewGuid():N}");

        (await EventuallyAsync(() => received() >= 1, TimeSpan.FromSeconds(10))).Should().BeTrue("版が進んだら届く");
        await listen.CancelAsync();
    }

    // 陰性の対照: 内容の変わらない突合（版が進まない）では届かない。
    [Fact]
    public async Task 版が進まない突合ではlist_changedは届かない()
    {
        var name = $"retrieval.search.{Guid.NewGuid():N}";
        Publish(name);
        var (client, received, acknowledged) = await ConnectAsync(factory, protocolVersion: null);
        await using var _ = client;
        var (listen, _) = await StartListenAsync(client, acknowledged);
        using var _listen = listen;

        Publish(name);

        (await EventuallyAsync(() => received() >= 1, TimeSpan.FromSeconds(2))).Should().BeFalse("版が進まなければ送らない");
        await listen.CancelAsync();
    }

    // ［#2315 のレビュー］tools の list_changed を求めない listen は、受付（扱う種類なし）の後にすぐ閉じる（接続を保持しない）。
    [Fact]
    public async Task toolsを求めないlistenは受付の後にすぐ閉じる()
    {
        var (client, _, acknowledged) = await ConnectAsync(factory, protocolVersion: null);
        await using var _c = client;

        var (cts, listen) = await StartListenAsync(client, acknowledged, toolsListChanged: false);
        using var _cts = cts;

        var completed = await Task.WhenAny(listen, Task.Delay(TimeSpan.FromSeconds(15), Ct));
        completed.Should().BeSameAs(listen, "送る通知が無いので保持しない");
        listen.IsCompletedSuccessfully.Should().BeTrue();
    }

    // ［#2315 の監査］listen は最長の寿命で閉じる（開き直すたびに認証を通り直す）。
    [Fact]
    public async Task listenは最長の寿命で閉じる()
    {
        await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ToolListChangedListenLimits>();
            s.AddSingleton(new ToolListChangedListenLimits { MaxLifetime = TimeSpan.FromSeconds(1) });
        }));
        var (client, _, acknowledged) = await ConnectAsync(host, protocolVersion: null);
        await using var _c = client;

        var (cts, listen) = await StartListenAsync(client, acknowledged);
        using var _cts = cts;

        var completed = await Task.WhenAny(listen, Task.Delay(TimeSpan.FromSeconds(15), Ct));
        completed.Should().BeSameAs(listen, "寿命に達したら空の結果で閉じる");
        listen.IsCompletedSuccessfully.Should().BeTrue();
    }

    // ［#2315 の監査］主体ごとの同時 listen は上限まで。超えた要求は断り、開いている listen は切らない。
    [Fact]
    public async Task 主体ごとの同時listenは上限を超えると断る()
    {
        await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ToolListChangedListenLimits>();
            s.AddSingleton(new ToolListChangedListenLimits { MaxPerSubject = 1 });
        }));
        var (client, _, acknowledged) = await ConnectAsync(host, protocolVersion: null);
        await using var _c = client;

        var (first, firstListen) = await StartListenAsync(client, acknowledged);
        using var _first = first;

        var second = async () => await StartListenAsync(client, acknowledged);
        await second.Should().ThrowAsync<ModelContextProtocol.McpProtocolException>();
        firstListen.IsCompleted.Should().BeFalse("開いている listen は切らない");
        await first.CancelAsync();
    }
}

// ［#2315 の監査］同時 listen の数え方（主体ごと・全体）。
[Trait("TestKind", "Unit")]
public class ToolListChangedListenLimitsTests
{
    [Fact]
    public void 主体ごとと全体の上限を数え解放で戻る()
    {
        var limits = new ToolListChangedListenLimits { MaxPerSubject = 2, MaxTotal = 3 };

        limits.TryAcquire("a").Should().BeTrue();
        limits.TryAcquire("a").Should().BeTrue();
        limits.TryAcquire("a").Should().BeFalse("主体ごとの上限");
        limits.TryAcquire("b").Should().BeTrue();
        limits.TryAcquire("c").Should().BeFalse("全体の上限");

        limits.Release("a");
        limits.TryAcquire("c").Should().BeTrue("解放で枠が戻る");
    }
}
