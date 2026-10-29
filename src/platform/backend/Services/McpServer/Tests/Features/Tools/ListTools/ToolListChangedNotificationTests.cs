using System.Text;
using AwesomeAssertions;
using McpServer.Domain;
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

    private async Task<(SdkMcpClient Client, Func<int> Received)> ConnectAsync(string? protocolVersion)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token());
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http, ownsHttpClient: true);
        var received = 0;
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
                ],
            },
        }, cancellationToken: Ct);
        return (client, () => Volatile.Read(ref received));
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
    private static async Task<CancellationTokenSource> ListenAsync(SdkMcpClient client)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listen = client.SendRequestAsync(new JsonRpcRequest
        {
            Method = RequestMethods.SubscriptionsListen,
            Params = System.Text.Json.JsonSerializer.SerializeToNode(
                new SubscriptionsListenRequestParams { Notifications = new() { ToolsListChanged = true } },
                ModelContextProtocol.McpJsonUtilities.DefaultOptions),
        }, cts.Token);
        // 受付が届くまでの間に版を進めると取りこぼすので、流れが開くのを待つ（取りこぼしは購読の順で防いでいるが、要求の到達は待つ）。
        await Task.Delay(500, Ct);
        if (listen.IsFaulted) await listen;
        return cts;
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
        var (client, received) = await ConnectAsync(protocolVersion: null);
        await using var _ = client;
        client.ServerCapabilities.Tools?.ListChanged.Should().BeTrue("list_changed を送ることを申告する");
        using var listen = await ListenAsync(client);

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
        var (client, received) = await ConnectAsync(protocolVersion: null);
        await using var _ = client;
        using var listen = await ListenAsync(client);

        Publish(name);

        (await EventuallyAsync(() => received() >= 1, TimeSpan.FromSeconds(2))).Should().BeFalse("版が進まなければ送らない");
        await listen.CancelAsync();
    }
}
