using AwesomeAssertions;
using LlmGateway.Domain.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using LlmGateway.Tests.Grpc;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Tests.Features.Completions.CompleteStream;

// IADR-0037, FR-11: 逐次生成が本文デルタと最終イベントを流し、egress 判定は一括と同一に
// 通すこと（拒否時はプロバイダを呼ばず理由のみ返す）を検証する。egress ゲート保持が最重要の検証点。
// ［2026-10-15 / #1902・[[IADR-0533]]］REST の `/complete/stream`（SSE）を撤去したので、同じ本体
// （`CompletionUseCase.StreamAsync`）を呼ぶ **gRPC 面**（`LlmCompletion/CompleteStream`）を TestServer 上で叩く。
// IADR-0110 (#395): メトリクス購読テスト（CompletionMetricsTests）と直列化する。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public class CompletionStreamEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // 許可経路: デルタ（スタブは "テスト回答" を 1 チャンク）と done イベント（sent=true・トークン数）を返す。
    [Fact]
    public async Task PostCompleteStream_Allowed_StreamsDeltaAndDone()
    {
        var req = new CompletionApiRequest("要約して", MaxTokens: 100, Confidentiality: "public", Purpose: "rag-answer");
        var events = await factory.CreateClient().CompleteStreamAsync(req, TestContext.Current.CancellationToken);

        string.Concat(events.Where(e => !e.Done).Select(e => e.Delta))
            .Should().Contain("テスト回答");             // 本文デルタ
        var done = events.Last();
        done.Done.Should().BeTrue();                     // 最終イベント
        done.Sent.Should().BeTrue();
        done.OutputTokens.Should().Be(20);               // スタブのトークン数が最終イベントで確定
    }

    // egress 拒否経路（越境保証の要）: 許容ティアの無い構成（confidential にティアCのみ）では
    // プロバイダを一切呼ばず、sent=false と理由のみを返す（本文デルタ "テスト回答" は現れない＝越境なし）。
    // ルーティング判定は一括と同一のため、逐次も同じゲートで拒否されることを検証する。
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

        var req = new CompletionApiRequest("機密文書の要約", MaxTokens: 100, Confidentiality: "confidential", Purpose: "analysis");
        var events = await client.CompleteStreamAsync(req, TestContext.Current.CancellationToken);

        var done = events.Should().ContainSingle().Subject;   // 本文デルタは 1 件も無い
        done.Done.Should().BeTrue();
        done.Sent.Should().BeFalse();
        (done.Text + done.RoutingReason).Should().Contain("拒否");
        events.Should().NotContain(e => (e.Delta + e.Text).Contains("テスト回答")); // プロバイダ未呼出（外部送信なし）
    }
}
