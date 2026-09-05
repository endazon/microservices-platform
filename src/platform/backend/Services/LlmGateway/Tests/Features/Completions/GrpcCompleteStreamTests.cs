using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using LlmGateway.Tests.Grpc;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace LlmGateway.Tests.Features.Completions;

// FR-04, FR-11, NFR-02, NFR-09, ADR-0010, ADR-0029, ADR-0075, ADR-0076 決定 5,
// IADR-0037, IADR-0104, IADR-0354, IADR-0379, IADR-0398 (#1255):
// 逐次生成の rpc（`LlmCompletion/CompleteStream`）を**実 Kestrel の h2c ポート**で往復する。
//
// 🔴 **本クラスの中心は「最初の delta が done より前に到着する」ことである**（T-P1-03）。
// `CompleteStream` をサーバストリーミングにした理由が NFR-02 の初回トークンの境界を保つことであり、
// **サーバが早く書くことは gRPC の保証ではなくコードの性質**だから、ここでしか守れない。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public class GrpcCompleteStreamTests
{
    private const string ServiceSubject = "service-account-aianalysis-service";
    private readonly GrpcKestrelFactory _factory;

    public GrpcCompleteStreamTests(GrpcKestrelFactory factory)
    {
        _factory = factory;
        _factory.StartServer();
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", $"Bearer {token}" } };

    private Pb.LlmCompletion.LlmCompletionClient PlainClient() =>
        new(GrpcChannel.ForAddress(_factory.GrpcAddress));

    private static string ServiceToken() =>
        GrpcKestrelFactory.IssueToken(ServiceSubject, [PlatformAuthPolicies.ServiceRole]);

    private static Pb.CompleteRequest Request(string prompt) => new()
    {
        Prompt = prompt,
        MaxTokens = 100,
        Confidentiality = "public",
        Purpose = "default",
    };

    private async Task<List<Pb.CompletionStreamEvent>> CollectAsync(Pb.CompleteRequest request)
    {
        using var call = PlainClient().CompleteStream(
            request, headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        var events = new List<Pb.CompletionStreamEvent>();
        await foreach (var ev in call.ResponseStream.ReadAllAsync(TestContext.Current.CancellationToken))
            events.Add(ev);
        return events;
    }

    // 🔴 T-P1-03: **最初の delta は done より前に到着する。**
    //
    // 偽プロバイダは 1 つ目の delta を即座に出し、2 つ目と done を StreamGap だけ遅らせる。
    // 判定は**到着時刻の差**で行う —— 絶対時刻で測ると遅い機械で偽陽性が出るが、
    // サーバがまとめてから書いていれば**差はほぼ 0**になるので、差なら機械の速さに依らない。
    //
    // これが落ちるとき起きているのは「gRPC が遅い」ではなく、**サーバ側（GrpcService か
    // CompletionUseCase）がイベントを溜めてから書いている**ことである。そのとき
    // AiAnalysis が north-south の最初の `token` を書く時刻は生成完了時刻まで遅れ、
    // `RagFirstTokenP95High` は応答完了 p95 を測る（ADR-0076 決定 5 が却下した形）。
    [Fact]
    public async Task First_delta_arrives_before_done()
    {
        using var call = PlainClient().CompleteStream(
            Request(ScriptedLlmProvider.SlowStreamMarker + " 本文"),
            headers: Bearer(ServiceToken()),
            cancellationToken: TestContext.Current.CancellationToken);

        var clock = Stopwatch.StartNew();
        TimeSpan? firstDeltaAt = null;
        TimeSpan? doneAt = null;

        await foreach (var ev in call.ResponseStream.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            if (!ev.Done && !string.IsNullOrEmpty(ev.Delta))
                firstDeltaAt ??= clock.Elapsed;
            if (ev.Done)
                doneAt ??= clock.Elapsed;
        }

        firstDeltaAt.Should().NotBeNull("最初の delta は独立したメッセージとして届く");
        doneAt.Should().NotBeNull();

        // 溜めてから書いていれば両者はほぼ同時に届く。ストリームしていれば差は StreamGap に近い。
        (doneAt!.Value - firstDeltaAt!.Value).Should().BeGreaterThan(ScriptedLlmProvider.StreamGap / 2,
            "最初の delta は done を待たずに送出されなければならない"
            + "（差が 0 に近いなら、サーバがイベントを溜めてからまとめて書いている）");
    }

    // 🔴 T-P1-04: **delta メッセージの `sent` は true である。**
    //
    // DTO の既定は `true`・proto3 の既定は `false` で**向きが逆**である（IADR-0398 決定 4）。
    // 写像が `Sent` を明示的に書き忘れると、例外は 1 つも起きず、
    // **全 delta が「縮退」に見える** —— 呼び出し元（AiAnalysis / Graph / Conversion）は
    // 縮退表示・提案 0 件・画像保持へ静かに倒れる。
    [Fact]
    public async Task Delta_messages_carry_sent_true()
    {
        var events = await CollectAsync(Request("本文"));

        var deltas = events.Where(e => !e.Done).ToList();
        deltas.Should().NotBeEmpty("陽性対照: delta が 1 件も出ていないなら本試験は何も守っていない");
        deltas.Should().OnlyContain(e => e.Sent,
            "proto3 の既定は false であり、サーバは delta にも sent=true を明示的に書く");

        // 陽性対照の対: 正常終了の done も sent=true である（縮退の done だけが false）。
        events.Last().Done.Should().BeTrue();
        events.Last().Sent.Should().BeTrue();
    }

    // T-P1-01: REST（SSE）と gRPC の `CompletionStreamEvent` 列が一致する ——
    // delta の列と、done の model / tokens / stop_reason。
    // 同じ判定器（CompletionUseCase）を通っていることの、契約側からの証明である。
    [Fact]
    public async Task Rest_sse_and_grpc_stream_produce_the_same_event_sequence()
    {
        var grpc = await CollectAsync(Request("本文"));

        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        var restEvents = await ReadSseAsync(http,
            new CompletionApiRequest("本文", MaxTokens: 100, Model: null,
                Confidentiality: "public", Purpose: "default"));

        // delta の列（本文の増分）が一致する。
        grpc.Where(e => !e.Done).Select(e => e.Delta)
            .Should().Equal(restEvents.Where(e => !e.Done).Select(e => e.Delta));

        var grpcDone = grpc.Single(e => e.Done);
        var restDone = restEvents.Single(e => e.Done);
        grpcDone.Sent.Should().Be(restDone.Sent);
        grpcDone.Model.Should().Be(restDone.Model);
        grpcDone.InputTokens.Should().Be(restDone.InputTokens);
        grpcDone.OutputTokens.Should().Be(restDone.OutputTokens);
        grpcDone.StopReason.Should().Be(restDone.StopReason ?? string.Empty);
        grpcDone.RoutingReason.Should().Be(restDone.RoutingReason ?? string.Empty);
    }

    // T-P1-01（続き）: モデルが拒否した場合（stop_reason=refusal）も両経路で一致する。
    // IADR-0104: 拒否でも `Sent=true` を保つ（越境監査・課金集計の意味を壊さない）。
    [Fact]
    public async Task Refusal_stop_reason_matches_between_rest_and_grpc()
    {
        var grpc = await CollectAsync(Request(ScriptedLlmProvider.RefusalMarker + " 本文"));

        using var http = new HttpClient { BaseAddress = new Uri(_factory.HttpAddress) };
        var rest = await ReadSseAsync(http,
            new CompletionApiRequest(ScriptedLlmProvider.RefusalMarker + " 本文", MaxTokens: 100,
                Model: null, Confidentiality: "public", Purpose: "default"));

        var grpcDone = grpc.Single(e => e.Done);
        var restDone = rest.Single(e => e.Done);

        grpcDone.StopReason.Should().Be(CompletionStopReasons.Refusal);
        grpcDone.StopReason.Should().Be(restDone.StopReason);
        grpcDone.Sent.Should().BeTrue("拒否は送信が成立したうえでの縮退である（IADR-0104）");
        grpcDone.Sent.Should().Be(restDone.Sent);
    }

    // 🔴 縮退は RpcException ではなく done=true / sent=false の**メッセージ**で返り、
    // ストリームは**正常終了する**（REST が SSE で 500 を伝播させないのと同値。IADR-0398 決定 5）。
    //
    // 観測点は上流の失敗である（既定構成ではティアB が有効で越境拒否は起こらない。実測）。
    [Fact]
    public async Task Upstream_failure_ends_the_stream_normally_with_sent_false()
    {
        var events = await CollectAsync(
            Request(ScriptedLlmProvider.UpstreamFailureMarker + " 本文"));

        events.Should().NotBeEmpty();
        var done = events.Last();
        done.Done.Should().BeTrue("ストリームは done で正常終了する（RpcException にしない）");
        done.Sent.Should().BeFalse();
        done.Text.Should().NotBeEmpty("縮退の理由が本文に載る");
    }

    // T-S-02 / T-S-03 の逐次版。s2s の面は一括 rpc と同じ強さでなければならない ——
    // 片方だけ緩いと、呼び出し元は緩いほうを使えば認可を迂回できる。
    [Fact]
    public async Task CompleteStream_without_credentials_is_unauthenticated()
    {
        using var call = PlainClient().CompleteStream(
            Request("本文"), cancellationToken: TestContext.Current.CancellationToken);

        var act = async () =>
        {
            await foreach (var _ in call.ResponseStream.ReadAllAsync(TestContext.Current.CancellationToken)) { }
        };

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task CompleteStream_with_forwarded_admin_user_token_is_permission_denied()
    {
        var userToken = GrpcKestrelFactory.IssueToken("admin-user", [PlatformAuthPolicies.AdminRole]);
        using var call = PlainClient().CompleteStream(
            Request("本文"), headers: Bearer(userToken),
            cancellationToken: TestContext.Current.CancellationToken);

        var act = async () =>
        {
            await foreach (var _ in call.ResponseStream.ReadAllAsync(TestContext.Current.CancellationToken)) { }
        };

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().Be(StatusCode.PermissionDenied);
    }

    // REST の SSE を読む（`data: ` 行の JSON を CompletionStreamEvent へ復元する）。
    private static async Task<List<CompletionStreamEvent>> ReadSseAsync(
        HttpClient http, CompletionApiRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/complete/stream")
        {
            Content = JsonContent.Create(body),
        };
        using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        var events = new List<CompletionStreamEvent>();
        await using var stream = await resp.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;
            var ev = JsonSerializer.Deserialize<CompletionStreamEvent>(
                line["data: ".Length..], new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (ev is not null)
                events.Add(ev);
        }
        return events;
    }
}
