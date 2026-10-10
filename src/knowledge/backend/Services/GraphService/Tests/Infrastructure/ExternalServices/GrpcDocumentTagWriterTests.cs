using System.Security.Claims;
using AwesomeAssertions;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.ExternalServices;
using GraphService.Tests.Grpc;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Knowledge.Contracts.Grpc.Document.V1;

namespace GraphService.Tests.Infrastructure.ExternalServices;

// FR-05, FR-18, NFR-09, NFR-16, SC-03, SC-05, ADR-0029, ADR-0036 D-07, ADR-0063 決定 1〜3,
// ADR-0075, ADR-0080, 計画 ADR-0086 決定 1・3, [[IADR-0044]], [[IADR-0364]], [[IADR-0379]],
// [[IADR-0410]] (#1255): タグ反映の gRPC 実装が、**REST 実装（`HttpDocumentTagWriter`）と
// 同じ枝・同じ値**であり、**承認者のトークンを面へ載せず利用者文脈を本文で運ぶ**ことを固定する。
//
// 🔴 fail-closed が本経路の不変条件である —— **承認できていないのに承認済みと見える**のが最悪であり、
// 陽性（`Applied`）と陰性（`Unavailable` / `NotWritable`）を対で置く。
[Trait("TestKind", "Unit")]
public class GrpcDocumentTagWriterTests
{
    private static readonly Guid DocumentId = Guid.NewGuid();

    // 🔴 T-01 陽性対照。**利用者文脈は本文で運ばれる**（`user_id` / 属性 / ロール / `action`）。
    // これが無いと、以下の陰性はすべて「何も送らない」実装でも緑になる。
    [Fact]
    public async Task 承認者の文脈を本文で運ぶ()
    {
        var fake = new FakeClient(new Pb.AddTagResponse { Result = Pb.TagWriteResult.Applied });

        var outcome = await Writer(fake, Approver("alice", clearance: "internal", roles: ["platform-admin"]))
            .AddTagAsync(DocumentId, "経理", TestContext.Current.CancellationToken);

        outcome.Should().Be(TagWriteOutcome.Applied);
        fake.LastRequest.Should().NotBeNull();
        fake.LastRequest!.UserId.Should().Be("alice");
        fake.LastRequest.Action.Should().Be(GraphAccessAction.Write, "action は既定へ丸めない");
        fake.LastRequest.UserRoles.Should().Contain(PlatformAuthPolicies.AdminRole);
        fake.LastRequest.UserAttributes.Should().Contain(
            new KeyValuePair<string, string>("clearance", "internal"));
        fake.LastRequest.DocumentId.Should().Be(DocumentId.ToString());
        fake.LastRequest.TagName.Should().Be("経理");
    }

    // 🔴 T-02: **ロールは `user_attributes` へ混ぜない。** ABAC 属性とロール判定は別の経路である
    // （計画 `07_abac-attribute-model` §利用者属性・`ADR-0080` 決定 4）。混ぜると 2 つの判定経路が
    // 1 つの地図の上で溶け、`UserAttributeEncoding` の集合値規則（`roles` は集合値ではない）とも食い違う。
    [Fact]
    public async Task ロールを利用者属性へ混ぜない()
    {
        var fake = new FakeClient(new Pb.AddTagResponse { Result = Pb.TagWriteResult.Applied });

        await Writer(fake, Approver("alice", roles: ["platform-admin", "platform-operator"]))
            .AddTagAsync(DocumentId, "経理", TestContext.Current.CancellationToken);

        fake.LastRequest!.UserAttributes.Should().NotContainKey("roles");
        fake.LastRequest.UserRoles.Should().HaveCount(2, "★ 陽性対照 —— ロールは別欄で運ばれている");
    }

    // 🔴 T-03: **承認者のトークンをメタデータへ載せない**（confused deputy の防止。
    // 計画 `ADR-0086` 決定 1 / [[IADR-0379]] 決定 4）。載るのは**チャネルに付いた s2s だけ**である。
    [Fact]
    public async Task 承認者のトークンをメタデータへ載せない()
    {
        var fake = new FakeClient(new Pb.AddTagResponse { Result = Pb.TagWriteResult.Applied });
        var accessor = Approver("alice");
        accessor.HttpContext!.Request.Headers.Authorization = "Bearer 承認者のトークン";

        await Writer(fake, accessor).AddTagAsync(DocumentId, "経理", TestContext.Current.CancellationToken);

        (fake.LastOptions.Headers ?? []).Should().BeEmpty(
            "利用者の資格情報は面を通らない —— 通ると呼び出し先が「利用者が直接呼んだ」と区別できない");
    }

    // 🔴 T-04: **応答は旧 REST の状態コードと 1:1 である**（撤去した REST のアダプタの試験と同じ母集合。
    // ［2026-10-10 / #1255］[[IADR-0533]]）。
    [Theory]
    [InlineData(Pb.TagWriteResult.Applied, TagWriteOutcome.Applied)]
    [InlineData(Pb.TagWriteResult.UnknownTag, TagWriteOutcome.UnknownTag)]
    [InlineData(Pb.TagWriteResult.NotWritable, TagWriteOutcome.NotWritable)]
    // 🔴 **未指定は成功に化けさせない。** proto3 の既定は 0 であり、受け口が代入を落とすと 0 が届く。
    [InlineData(Pb.TagWriteResult.Unspecified, TagWriteOutcome.Unavailable)]
    public async Task 応答をRESTと同じ結果へ写す(Pb.TagWriteResult result, TagWriteOutcome expected)
    {
        var fake = new FakeClient(new Pb.AddTagResponse { Result = result });

        var outcome = await Writer(fake, Approver("alice"))
            .AddTagAsync(DocumentId, "経理", TestContext.Current.CancellationToken);

        outcome.Should().Be(expected);
    }

    // 🔴 T-05: **輸送の失敗はすべて `Unavailable`**（REST の非 2xx・不達と同じ値）。
    // **成功へ縮退しない。**
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.InvalidArgument)]
    [InlineData(StatusCode.Internal)]
    public async Task 輸送の失敗は到達不能へ倒す(StatusCode status)
    {
        var fake = new FakeClient(new RpcException(new Status(status, "失敗")));

        var outcome = await Writer(fake, Approver("alice"))
            .AddTagAsync(DocumentId, "経理", TestContext.Current.CancellationToken);

        outcome.Should().Be(TagWriteOutcome.Unavailable);
    }

    // 🔴 T-06: s2s トークンの取得失敗も**同じ縮退**である。
    [Fact]
    public async Task s2sトークン取得失敗も到達不能である()
    {
        var fake = new FakeClient(new InvalidOperationException("ServiceToken:ClientId が未設定です。"));

        var outcome = await Writer(fake, Approver("alice"))
            .AddTagAsync(DocumentId, "経理", TestContext.Current.CancellationToken);

        outcome.Should().Be(TagWriteOutcome.Unavailable);
    }

    // 🔴 T-07: **呼び出し元のキャンセルだけは伝播する**（#1637 で本物のチャネルの形へ改めた）。
    // 127.0.0.1 の実サーバーで受け口が要求を受け取ってから呼び出し元が取り消す。本物のチャネルはこれを
    // `RpcException(Cancelled)` で投げる（下の T-07a）ので、素の OCE を注入する形では `catch (RpcException)` が
    // 取り消しを `Unavailable`（呼び出し側は 502）へ畳んでも緑になっていた。
    // **外へ出るのは呼び出し元の token を持つ OCE で、縮退の Error ログは出ない**ことを測る。
    [Fact]
    public async Task 呼び出し元のキャンセルは伝播する()
    {
        var service = new TagWriteService(ServerBehavior.Hang);
        await using var server = await LoopbackGrpcServer.StartAsync(service, Ct);
        var logger = new RecordingLogger<GrpcDocumentTagWriter>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var writer = new GrpcDocumentTagWriter(
            new Pb.DocumentTagWrite.DocumentTagWriteClient(server.Channel), Approver("alice"), logger);

        var call = writer.AddTagAsync(DocumentId, "経理", cts.Token);
        await service.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();

        var thrown = await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cts.Token, "呼び出し元の取り消しとして外へ出す");
        logger.OfLevel(LogLevel.Error).Should().BeEmpty("取り消しは反映の失敗ではない");
    }

    // T-07a 前提の表明: 本物のチャネルは呼び出し元の取り消しを `RpcException(Cancelled)` で投げる。
    // これが崩れる（チャネルが OCE を投げる）と、T-07 は取り消しの捕捉を測らなくなる。
    [Fact]
    public async Task 前提_本物のチャネルは取り消しを_RpcException_Cancelled_で投げる()
    {
        var service = new TagWriteService(ServerBehavior.Hang);
        await using var server = await LoopbackGrpcServer.StartAsync(service, Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var client = new Pb.DocumentTagWrite.DocumentTagWriteClient(server.Channel);

        var call = client.AddTagAsync(new Pb.AddTagRequest(), cancellationToken: cts.Token).ResponseAsync;
        await service.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();

        var thrown = await FluentActions.Awaiting(() => call).Should().ThrowAsync<RpcException>();
        thrown.Which.StatusCode.Should().Be(StatusCode.Cancelled);
    }

    // 🔴 T-07b: **呼び出し元が取り消していない `CANCELLED` は従来どおり `Unavailable` である。**
    // 受け口が返した `CANCELLED`（呼び出し元の ct は生きている）を取り消しとして外へ出すと、
    // status だけで判定する変異（`when (ex.StatusCode == StatusCode.Cancelled)`）と区別できない。
    [Fact]
    public async Task 受け口が返した_Cancelled_は到達不能へ倒す()
    {
        var service = new TagWriteService(ServerBehavior.ReturnCancelled);
        await using var server = await LoopbackGrpcServer.StartAsync(service, Ct);
        var logger = new RecordingLogger<GrpcDocumentTagWriter>();
        var writer = new GrpcDocumentTagWriter(
            new Pb.DocumentTagWrite.DocumentTagWriteClient(server.Channel), Approver("alice"), logger);

        var outcome = await writer.AddTagAsync(DocumentId, "経理", Ct);

        outcome.Should().Be(TagWriteOutcome.Unavailable);
        logger.OfLevel(LogLevel.Error).Should().ContainSingle("★ 陽性対照 —— 縮退の枝は Error を出す");
    }

    // 🔴 T-08: **承認者が分からなければ呼ばない。値は REST と同じ `NotWritable` である。**
    // REST 版は資格情報なしで呼び、後段が匿名として 404 を返すので `NotWritable` になっていた。
    // 空の `user_id` を送ると後段は `INVALID_ARGUMENT` を返し、それは `Unavailable` へ落ちる ——
    // **値が変わってしまう**ので手前で同じ値へ倒す。
    [Fact]
    public async Task 承認者が分からなければ呼ばず書けないを返す()
    {
        var fake = new FakeClient(new Pb.AddTagResponse { Result = Pb.TagWriteResult.Applied });

        var outcome = await Writer(fake, Anonymous())
            .AddTagAsync(DocumentId, "経理", TestContext.Current.CancellationToken);

        outcome.Should().Be(TagWriteOutcome.NotWritable);
        fake.LastRequest.Should().BeNull("資格情報を発明して呼ばない");
    }

    // 🔴 T-09 / T-10: **宛先の有無で登録の形が変わる**（未設定は UNAVAILABLE の呼び出し器。[[IADR-0533]] 決定 2。登録関数を陽性・陰性の対で固定する）。
    [Fact]
    public void 宛先が未設定でも届かない宛先として登録する()
    {
        // ［2026-10-10 / #1255］[[IADR-0533]] 決定 2: 未設定でも生成クライアントは登録され、呼び出しは UNAVAILABLE で失敗する
        // （従前は何も登録せず、`Program.cs` が REST 実装へ倒していた。REST 実装は撤去した）。
        var services = new ServiceCollection()
            .AddDocumentTagWriteGrpcClient(new ConfigurationBuilder().Build());

        services.Should().ContainSingle(d => d.ServiceType == typeof(Pb.DocumentTagWrite.DocumentTagWriteClient));
    }

    [Fact]
    public void 宛先が構成されていれば生成クライアントを登録する()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DocumentTagWriteGrpcClientExtensions.AddressKey] = "http://document-service:8081",
        }).Build();

        var services = new ServiceCollection().AddDocumentTagWriteGrpcClient(config);

        services.Should().Contain(
            d => d.ServiceType == typeof(Pb.DocumentTagWrite.DocumentTagWriteClient),
            "★ 陽性対照 —— 登録されないのは関数が壊れているからではない");
    }

    // ── 器 ────────────────────────────────────────────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private enum ServerBehavior { Hang, ReturnCancelled }

    // 実サーバーに載せる受け口の偽物。`Hang` は要求を受け取ったことを知らせてから取り消されるまで待ち、
    // `ReturnCancelled` は受け口自身が `CANCELLED` を返す（呼び出し元の取り消しではない対照）。
    private sealed class TagWriteService(ServerBehavior behavior) : Pb.DocumentTagWrite.DocumentTagWriteBase
    {
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<Pb.AddTagResponse> AddTag(Pb.AddTagRequest request, ServerCallContext context)
        {
            Received.TrySetResult();
            if (behavior == ServerBehavior.ReturnCancelled)
                throw new RpcException(new Status(StatusCode.Cancelled, "受け口が取り消した"));
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            return new Pb.AddTagResponse();
        }
    }

    private static GrpcDocumentTagWriter Writer(
        Pb.DocumentTagWrite.DocumentTagWriteClient client, IHttpContextAccessor accessor) =>
        new(client, accessor, NullLogger<GrpcDocumentTagWriter>.Instance);

    private static IHttpContextAccessor Anonymous() => new StubAccessor(new DefaultHttpContext());

    private static IHttpContextAccessor Approver(
        string name, string? clearance = null, string[]? roles = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        if (clearance is not null) claims.Add(new Claim("clearance", clearance));
        foreach (var role in roles ?? []) claims.Add(new Claim(ClaimTypes.Role, role));
        return new StubAccessor(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        });
    }

    private sealed class StubAccessor(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set => throw new NotSupportedException(); }
    }

    private sealed class FakeClient : Pb.DocumentTagWrite.DocumentTagWriteClient
    {
        private readonly Task<Pb.AddTagResponse> _response;

        public FakeClient(Pb.AddTagResponse response) => _response = Task.FromResult(response);

        public FakeClient(Exception exception) =>
            _response = Task.FromException<Pb.AddTagResponse>(exception);

        public Pb.AddTagRequest? LastRequest { get; private set; }
        public CallOptions LastOptions { get; private set; }

        public override AsyncUnaryCall<Pb.AddTagResponse> AddTagAsync(
            Pb.AddTagRequest request, CallOptions options)
        {
            LastRequest = request;
            LastOptions = options;
            return new AsyncUnaryCall<Pb.AddTagResponse>(
                _response, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }
    }
}
