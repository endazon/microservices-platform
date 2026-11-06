using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using Grpc.Core;
using LlmGateway.Tests.Grpc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace LlmGateway.Tests.Features.Authorization;

// NFR-18, NFR-09, FR-11, ADR-0084 決定 1, IADR-0684 (#2417): 呼び手ごとの用途とモデルの指定の許可表を、
// 本物のホスト（REST と gRPC の両方の面）で固定する。
//
// 🔴 **段 1（既定 Report）は止めない。段 2（Enforce）は許可表に無い呼び出しを REST 403 / gRPC PERMISSION_DENIED で拒む。**
// 設定キー・計器の名前は配備と監視が読む外部の契約なので、綴りで書く。
// 既定の許可表（appsettings.json）: retrieval-service は rerank だけ、graph-service は graph-suggestion・graph-cluster-summary だけ。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public sealed class CallerPurposePolicyTests : IDisposable
{
    private const string ViolationCounter = "llm.caller_policy.violation.total";

    private readonly TestWebApplicationFactory _rest = new();
    private readonly GrpcKestrelFactory _grpc;

    public CallerPurposePolicyTests(GrpcKestrelFactory grpc)
    {
        _grpc = grpc;
        _grpc.StartServer();
    }

    public void Dispose() => _rest.Dispose();

    private WebApplicationFactory<Program> Enforce() =>
        _rest.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:CallerPolicy:Mode"] = "Enforce" })));

    private static HttpClient As(WebApplicationFactory<Program> app, string subject)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestServiceTokens.IssueToken(subject, [PlatformAuthPolicies.ServiceRole]));
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string purpose, string? model = null) =>
        client.PostAsJsonAsync("/complete",
            new CompletionApiRequest("質問", MaxTokens: 64, Model: model, Confidentiality: "public", Purpose: purpose),
            TestContext.Current.CancellationToken);

    // ---- 段 2（Enforce）: REST ------------------------------------------------------------------------

    [Fact]
    public async Task Enforceでは許可表にある用途は通る()
    {
        using var app = Enforce();

        (await PostAsync(As(app, "service-account-retrieval-service"), "rerank")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Enforceでは許可表に無い用途を403で拒む()
    {
        using var app = Enforce();

        (await PostAsync(As(app, "service-account-retrieval-service"), "analysis")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync(As(app, "service-account-retrieval-service"), "unknown-purpose")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Enforceでは許可表に無い呼び手を403で拒む()
    {
        using var app = Enforce();

        (await PostAsync(As(app, "service-account-wiki-service"), "rerank")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Enforceではモデルの明示の指定を許されていない呼び手を403で拒む()
    {
        using var app = Enforce();

        (await PostAsync(As(app, "service-account-retrieval-service"), "rerank", model: "claude-opus-5-5"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- 段 1（Report・既定）: 止めずに数える（gRPC の面で配線を固定する） --------------------------------

    [Fact]
    public async Task 既定の段ではgRPCでも許可表に無い用途を止めずに計器へ数える()
    {
        var seen = new List<Dictionary<string, string>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Name == ViolationCounter) l.EnableMeasurementEvents(i);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var d = new Dictionary<string, string>();
            foreach (var t in tags) d[t.Key] = t.Value?.ToString() ?? string.Empty;
            lock (seen) seen.Add(d);
        });
        listener.Start();

        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            _grpc.GrpcAddress,
            new FixedTokenProvider(GrpcKestrelFactory.IssueToken("service-account-graph-service", [PlatformAuthPolicies.ServiceRole])));
        var resp = await new Pb.LlmCompletion.LlmCompletionClient(channel).CompleteAsync(
            new Pb.CompleteRequest { Prompt = "本文", MaxTokens = 10, Confidentiality = "public", Purpose = "rag-answer" },
            cancellationToken: TestContext.Current.CancellationToken);

        resp.Sent.Should().BeTrue("段 1 は送信を止めない");
        List<Dictionary<string, string>> snapshot;
        lock (seen) snapshot = [.. seen];
        snapshot.Should().Contain(m =>
            m["llm.caller"] == "graph-service"
            && m["llm.policy.kind"] == "purpose_not_allowed"
            && m["llm.policy.mode"] == "report");
    }

    [Fact]
    public async Task Enforceでも許可表にある用途はgRPCの面を通る_既定の段の試験と対にする()
    {
        // gRPC の器は共有なので段 2 を組めない。段 2 の拒否は REST で、gRPC は「同じ判定器を呼ぶ」ことを段 1 の計器で固定する。
        using var channel = GrpcClientExtensions.CreatePlatformChannel(
            _grpc.GrpcAddress,
            new FixedTokenProvider(GrpcKestrelFactory.IssueToken("service-account-graph-service", [PlatformAuthPolicies.ServiceRole])));
        var resp = await new Pb.LlmCompletion.LlmCompletionClient(channel).CompleteAsync(
            new Pb.CompleteRequest { Prompt = "本文", MaxTokens = 10, Confidentiality = "public", Purpose = "graph-suggestion" },
            cancellationToken: TestContext.Current.CancellationToken);

        resp.Sent.Should().BeTrue();
    }

    // AI レビュー #2429: gRPC でも段 2 の拒否（PERMISSION_DENIED）を実測する。gRPC の器は共有で構成を差し替えられないので、
    // 判定器が毎回読む IOptionsMonitor の現在値の段だけを、この試験の間だけ Enforce にして元へ戻す
    // （SharedMeterCollection の試験は直列に走る）。
    [Fact]
    public async Task 段2ではgRPCでも許可表に無い用途をPERMISSION_DENIEDで拒む()
    {
        var options = _grpc.Services.GetRequiredService<IOptionsMonitor<LlmGateway.Domain.Authorization.LlmCallerPolicyOptions>>().CurrentValue;
        var before = options.Mode;
        options.Mode = LlmGateway.Domain.Authorization.LlmCallerPolicyMode.Enforce;
        try
        {
            using var channel = GrpcClientExtensions.CreatePlatformChannel(
                _grpc.GrpcAddress,
                new FixedTokenProvider(GrpcKestrelFactory.IssueToken("service-account-graph-service", [PlatformAuthPolicies.ServiceRole])));
            var client = new Pb.LlmCompletion.LlmCompletionClient(channel);

            var denied = async () => await client.CompleteAsync(
                new Pb.CompleteRequest { Prompt = "本文", MaxTokens = 10, Confidentiality = "public", Purpose = "rag-answer" },
                cancellationToken: TestContext.Current.CancellationToken);
            (await denied.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);

            var stream = async () =>
            {
                using var call = client.CompleteStream(
                    new Pb.CompleteRequest { Prompt = "本文", MaxTokens = 10, Confidentiality = "public", Purpose = "rag-answer" },
                    cancellationToken: TestContext.Current.CancellationToken);
                while (await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken)) { }
            };
            (await stream.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);

            (await client.CompleteAsync(
                    new Pb.CompleteRequest { Prompt = "本文", MaxTokens = 10, Confidentiality = "public", Purpose = "graph-suggestion" },
                    cancellationToken: TestContext.Current.CancellationToken))
                .Sent.Should().BeTrue("許可表にある用途は段 2 でも通る");
        }
        finally
        {
            options.Mode = before;
        }
    }

    private sealed class FixedTokenProvider(string token) : IServiceTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct) => ValueTask.FromResult(token);
    }
}
