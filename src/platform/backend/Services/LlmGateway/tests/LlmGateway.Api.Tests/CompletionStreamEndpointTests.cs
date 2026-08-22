using AwesomeAssertions;
using LlmGateway.Api.Foundation.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace LlmGateway.Api.Tests;

// IADR-0037, FR-11: /complete/stream（SSE）が本文デルタと最終イベントを流し、egress 判定は /complete と同一に
// 通すこと（拒否時はプロバイダを呼ばず理由のみ返す）を検証する。egress ゲート保持が最重要の検証点。
// IADR-0110 (#395): メトリクス購読テスト（CompletionMetricsTests）と直列化する。
[Collection(CompletionEndpointCollection.Name)]
public class CompletionStreamEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // 許可経路: デルタ（スタブは "テスト回答" を 1 チャンク）と done イベント（sent=true・トークン数）を返す。
    [Fact]
    public async Task PostCompleteStream_Allowed_StreamsDeltaAndDone()
    {
        var req = new { Prompt = "要約して", MaxTokens = 100, Confidentiality = "public", Purpose = "rag-answer" };
        var resp = await factory.CreateClient().PostAsJsonAsync("/complete/stream", req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().Contain("テスト回答");        // 本文デルタ
        body.Should().Contain("\"done\":true");       // 最終イベント
        body.Should().Contain("\"sent\":true");
        body.Should().Contain("\"outputTokens\":20"); // スタブのトークン数が最終イベントで確定
    }

    // egress 拒否経路（越境保証の要）: 許容ティアの無い構成（confidential にティアCのみ）では
    // プロバイダを一切呼ばず、sent=false と理由のみを返す（本文デルタ "テスト回答" は現れない＝越境なし）。
    // ルーティング判定は /complete と同一のため、/complete/stream も同じゲートで拒否されることを検証する。
    [Fact]
    public async Task PostCompleteStream_Denied_EmitsRefusalWithoutCallingProvider()
    {
        var client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
                s.Configure<LlmRoutingOptions>(o =>
                {
                    o.AllowUnapprovedTierC = false;
                    o.Endpoints =
                    [
                        new LlmEndpointOptions
                        {
                            Name = "standard-external", Tier = ProtectionTier.C, Provider = "claude",
                            Enabled = true, Priority = 1, DefaultModel = "std", Models = ["std"]
                        }
                    ];
                }))).CreateClient();

        var req = new { Prompt = "機密文書の要約", MaxTokens = 100, Confidentiality = "confidential", Purpose = "analysis" };
        var resp = await client.PostAsJsonAsync("/complete/stream", req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().Contain("\"sent\":false");
        body.Should().Contain("拒否");
        body.Should().NotContain("テスト回答");       // プロバイダ未呼出（外部送信なし）
    }
}
