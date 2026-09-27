using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Tests.Testing;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Authz;

// FR-03, FR-05, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0379, IADR-0401 (#1646):
// 認可サービスの共有クライアント 2 つ（`AuthzScopeGrpcClient`・`UserDirectoryGrpcClient`）が、
// **呼び出し元の取り消しを「引けなかった」（`null`）や deny へ畳まない**ことを、本物のチャネルで固定する。
//
// 🔴 本物のチャネルは呼び出し元の取り消しを `RpcException(Cancelled)` で投げる（`ThrowOperationCanceledOnCancellation`
// は既定の false）。無条件の `catch (RpcException)` はそれを `null` へ畳み、呼び出し元は取り消された要求を
// 「認可サービスの障害」（502・Unavailable・deny）として扱っていた。偽のクライアントへ素の OCE を注入する形では
// この経路を通らないので、127.0.0.1 の実サーバーで**受け口が要求を受け取ってから**取り消す。
//
// 🔴 対（受け口が返した `CANCELLED` は従来どおり `null`・Warning）を同じ読み口の全部に置く ——
// status で判定する変異（`when (ex.StatusCode == StatusCode.Cancelled)`）は対の側で赤になる。
[Trait("TestKind", "Unit")]
public class AuthzGrpcClientCallerCancellationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 読み口の全部（本番の呼び出し元が使う公開の口）。`ResolveScopeAsync` は `TryResolveScopeAsync` の上の deny への畳み込みで、
    // 呼び出し元（AiAnalysis / Graph / Retrieval / Wiki の各解決器）が最も多い口なので別に数える。
    public static TheoryData<string> Operations =>
    [
        nameof(AuthzScopeGrpcClient.ResolveAsync),
        nameof(AuthzScopeGrpcClient.ResolveScopeAsync),
        nameof(AuthzScopeGrpcClient.TryResolveScopeAsync),
        nameof(UserDirectoryGrpcClient.CheckUsernamesAsync),
        nameof(UserDirectoryGrpcClient.CheckDepartmentCodesAsync),
        nameof(UserDirectoryGrpcClient.GetUserAttributesAsync),
        nameof(UserDirectoryGrpcClient.HasRealmRoleAsync),
        nameof(UserDirectoryGrpcClient.GetRetentionStatusAsync),
        nameof(UserDirectoryGrpcClient.GetAccountStatusAsync),
    ];

    // 前提の表明: 本物のチャネルは呼び出し元の取り消しを `RpcException(Cancelled)` で投げる。
    // これが崩れる（チャネルが OCE を投げる）と、下の試験は共有クライアントの守りを測らなくなる。
    [Fact]
    public async Task 前提_本物のチャネルは取り消しを_RpcException_Cancelled_で投げる()
    {
        var (scope, directory) = (new FakeAuthzScope(Behavior.Hang), new FakeUserDirectory(Behavior.Hang));
        await using var server = await LoopbackGrpcServer.StartAsync(scope, directory, Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = new Pb.UserDirectory.UserDirectoryClient(server.Channel)
            .GetUserAttributesAsync(new Pb.GetUserAttributesRequest { Username = "alice" }, cancellationToken: cts.Token)
            .ResponseAsync;
        await directory.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();

        var thrown = await FluentActions.Awaiting(() => call).Should().ThrowAsync<RpcException>();
        thrown.Which.StatusCode.Should().Be(StatusCode.Cancelled);
    }

    // 🔴 AC-2: **呼び出し元の取り消しは、呼び出し元の token を持つ OCE で外へ出る。** 縮退の Warning は出ない。
    [Theory]
    [MemberData(nameof(Operations))]
    public async Task 呼び出し元の取り消しは引けなかったへ畳まず外へ出す(string operation)
    {
        var (scope, directory) = (new FakeAuthzScope(Behavior.Hang), new FakeUserDirectory(Behavior.Hang));
        await using var server = await LoopbackGrpcServer.StartAsync(scope, directory, Ct);
        var (scopeLog, directoryLog) = (new RecordingLogger<AuthzScopeGrpcClient>(), new RecordingLogger<UserDirectoryGrpcClient>());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = Invoke(operation, server, scopeLog, directoryLog, cts.Token);
        await Task.WhenAny(scope.Received.Task, directory.Received.Task).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();

        var thrown = await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cts.Token, "呼び出し元の取り消しとして外へ出す");
        scopeLog.Entries.Should().BeEmpty("取り消しは認可サービスの障害ではない");
        directoryLog.Entries.Should().BeEmpty("取り消しは利用者名簿の障害ではない");
    }

    // 🔴 対: **呼び出し元が取り消していない `CANCELLED`（受け口が返したもの）は従来どおり「引けなかった」**である。
    [Theory]
    [MemberData(nameof(Operations))]
    public async Task 受け口が返した_Cancelled_は従来どおり引けなかったである(string operation)
    {
        var (scope, directory) = (new FakeAuthzScope(Behavior.ReturnCancelled), new FakeUserDirectory(Behavior.ReturnCancelled));
        await using var server = await LoopbackGrpcServer.StartAsync(scope, directory, Ct);
        var (scopeLog, directoryLog) = (new RecordingLogger<AuthzScopeGrpcClient>(), new RecordingLogger<UserDirectoryGrpcClient>());

        var result = await Invoke(operation, server, scopeLog, directoryLog, Ct);

        if (operation == nameof(AuthzScopeGrpcClient.ResolveScopeAsync))
            result.Should().BeOfType<Platform.Shared.Contracts.Dtos.AccessScopeResponse>()
                .Which.Granted.Should().BeFalse("deny-by-default へ畳む口である");
        else
            result.Should().BeNull();
        (scopeLog.OfLevel(LogLevel.Warning).Count + directoryLog.OfLevel(LogLevel.Warning).Count)
            .Should().Be(1, "★ 陽性対照 —— 縮退の枝は Warning を出す");
    }

    private static readonly IReadOnlyDictionary<string, string> NoAttributes = new Dictionary<string, string>();

    private static async Task<object?> Invoke(
        string operation, LoopbackGrpcServer server,
        RecordingLogger<AuthzScopeGrpcClient> scopeLog, RecordingLogger<UserDirectoryGrpcClient> directoryLog,
        CancellationToken ct)
    {
        var scopes = new AuthzScopeGrpcClient(new Pb.AuthzScope.AuthzScopeClient(server.Channel), scopeLog);
        var directory = new UserDirectoryGrpcClient(new Pb.UserDirectory.UserDirectoryClient(server.Channel), directoryLog);
        return operation switch
        {
            nameof(AuthzScopeGrpcClient.ResolveAsync) => await scopes.ResolveAsync("alice", NoAttributes, "read", ct),
            nameof(AuthzScopeGrpcClient.ResolveScopeAsync) => await scopes.ResolveScopeAsync("alice", NoAttributes, "read", ct),
            nameof(AuthzScopeGrpcClient.TryResolveScopeAsync) => await scopes.TryResolveScopeAsync("alice", NoAttributes, "read", ct),
            nameof(UserDirectoryGrpcClient.CheckUsernamesAsync) => await directory.CheckUsernamesAsync(["alice"], ct),
            nameof(UserDirectoryGrpcClient.CheckDepartmentCodesAsync) => await directory.CheckDepartmentCodesAsync(["sales"], ct),
            nameof(UserDirectoryGrpcClient.GetUserAttributesAsync) => await directory.GetUserAttributesAsync("alice", ct),
            nameof(UserDirectoryGrpcClient.HasRealmRoleAsync) => await directory.HasRealmRoleAsync("alice", "platform-admin", ct),
            nameof(UserDirectoryGrpcClient.GetRetentionStatusAsync) => await directory.GetRetentionStatusAsync("alice", ct),
            nameof(UserDirectoryGrpcClient.GetAccountStatusAsync) => await directory.GetAccountStatusAsync("alice", ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "試験の器を更新すること"),
        };
    }

    private enum Behavior { Hang, ReturnCancelled }

    // 受け口の偽物に共通の振る舞い。`Hang` は要求を受け取ったことを知らせてから取り消されるまで待ち、
    // `ReturnCancelled` は受け口自身が `CANCELLED` を返す（呼び出し元の取り消しではない対照）。
    private static async Task<T> Answer<T>(Behavior behavior, TaskCompletionSource received, ServerCallContext context)
    {
        received.TrySetResult();
        if (behavior == Behavior.ReturnCancelled)
            throw new RpcException(new Status(StatusCode.Cancelled, "受け口が取り消した"));
        await Task.Delay(Timeout.Infinite, context.CancellationToken);
        throw new InvalidOperationException("unreachable");
    }

    private sealed class FakeAuthzScope(Behavior behavior) : Pb.AuthzScope.AuthzScopeBase
    {
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<Pb.ResolveScopeResponse> Resolve(Pb.ResolveScopeRequest request, ServerCallContext context)
            => Answer<Pb.ResolveScopeResponse>(behavior, Received, context);
    }

    private sealed class FakeUserDirectory(Behavior behavior) : Pb.UserDirectory.UserDirectoryBase
    {
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<Pb.CheckUsernamesResponse> CheckUsernames(Pb.CheckUsernamesRequest request, ServerCallContext context)
            => Answer<Pb.CheckUsernamesResponse>(behavior, Received, context);

        public override Task<Pb.CheckDepartmentCodesResponse> CheckDepartmentCodes(
            Pb.CheckDepartmentCodesRequest request, ServerCallContext context)
            => Answer<Pb.CheckDepartmentCodesResponse>(behavior, Received, context);

        public override Task<Pb.GetUserAttributesResponse> GetUserAttributes(
            Pb.GetUserAttributesRequest request, ServerCallContext context)
            => Answer<Pb.GetUserAttributesResponse>(behavior, Received, context);

        public override Task<Pb.CheckRealmRoleResponse> CheckRealmRole(Pb.CheckRealmRoleRequest request, ServerCallContext context)
            => Answer<Pb.CheckRealmRoleResponse>(behavior, Received, context);
    }
}
