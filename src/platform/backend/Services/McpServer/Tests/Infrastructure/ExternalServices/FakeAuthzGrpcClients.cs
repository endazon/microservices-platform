using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Grpc.Authz.V1;
using Platform.Shared.Infrastructure.Foundation.Authz;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// FR-16, FR-05, UC-09, SC-12, NFR-09, ADR-0029, ADR-0075, [[IADR-0379]], [[IADR-0401]] (#1255):
// east-west gRPC の**生成クライアントの偽物**。生成された `*Client` は仮想メソッドと
// protected な既定コンストラクタを持つので、実チャネル無しで差し替えられる。
//
// 🔴 **記録するのは「何を送ったか」である。** 登録者が**自分自身**の名前でしか引かないこと
// （[[IADR-0401]] 決定 2 の前提）は、送った `username` を見ないと固定できない。
internal sealed class FakeUserDirectoryClient : UserDirectory.UserDirectoryClient
{
    private readonly GetUserAttributesResponse? _response;
    private readonly StatusCode? _failWith;

    private FakeUserDirectoryClient(GetUserAttributesResponse? response, StatusCode? failWith)
    {
        _response = response;
        _failWith = failWith;
    }

    /// <summary>要求された `username`（最後の 1 件）。</summary>
    public string? LastRequestedUsername { get; private set; }

    /// <summary>何回呼ばれたか。**呼ばないこと**を表明するために使う。</summary>
    public int CallCount { get; private set; }

    public static FakeUserDirectoryClient Returning(
        string username, IReadOnlyDictionary<string, string> attributes)
    {
        var resp = new GetUserAttributesResponse { Found = true, Username = username };
        foreach (var (k, v) in attributes) resp.Attributes[k] = v;
        return new FakeUserDirectoryClient(resp, null);
    }

    public static FakeUserDirectoryClient NotFound() =>
        new(new GetUserAttributesResponse { Found = false }, null);

    public static FakeUserDirectoryClient Failing(StatusCode status) => new(null, status);

    public override AsyncUnaryCall<GetUserAttributesResponse> GetUserAttributesAsync(
        GetUserAttributesRequest request, CallOptions options)
    {
        CallCount++;
        LastRequestedUsername = request.Username;
        return _failWith is { } status
            ? FakeGrpcCalls.Failing<GetUserAttributesResponse>(status)
            : FakeGrpcCalls.Returning(_response!);
    }
}

internal sealed class FakeAuthzScopeClient : AuthzScope.AuthzScopeClient
{
    private readonly ResolveScopeResponse? _response;
    private readonly StatusCode? _failWith;

    private FakeAuthzScopeClient(ResolveScopeResponse? response, StatusCode? failWith)
    {
        _response = response;
        _failWith = failWith;
    }

    /// <summary>最後に送った要求（`action` と `user_id` の固定に使う）。</summary>
    public ResolveScopeRequest? LastRequest { get; private set; }

    public int CallCount { get; private set; }

    public static FakeAuthzScopeClient Returning(ResolveScopeResponse response) => new(response, null);

    public static FakeAuthzScopeClient Failing(StatusCode status) => new(null, status);

    public override AsyncUnaryCall<ResolveScopeResponse> ResolveAsync(
        ResolveScopeRequest request, CallOptions options)
    {
        CallCount++;
        LastRequest = request;
        return _failWith is { } status
            ? FakeGrpcCalls.Failing<ResolveScopeResponse>(status)
            : FakeGrpcCalls.Returning(_response!);
    }
}

// 生成クライアントが返す `AsyncUnaryCall<T>` を組み立てる（成功／失敗）。
internal static class FakeGrpcCalls
{
    public static AsyncUnaryCall<T> Returning<T>(T response) => new(
        Task.FromResult(response), Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess, () => [], () => { });

    public static AsyncUnaryCall<T> Failing<T>(StatusCode status)
    {
        var ex = new RpcException(new Status(status, "fake"));
        return new AsyncUnaryCall<T>(
            Task.FromException<T>(ex), Task.FromResult(new Metadata()),
            () => new Status(status, "fake"), () => [], () => { });
    }
}

// 偽の生成クライアントを本物のラッパへ差し込むための組み立て。
internal static class FakeAuthzGrpc
{
    public static UserDirectoryGrpcClient Directory(UserDirectory.UserDirectoryClient client)
        => new(client, NullLogger<UserDirectoryGrpcClient>.Instance);

    public static AuthzScopeGrpcClient Scopes(AuthzScope.AuthzScopeClient client)
        => new(client, NullLogger<AuthzScopeGrpcClient>.Instance);
}
