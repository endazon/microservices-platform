using Grpc.Core;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace DocumentService.Tests.Infrastructure.ExternalServices;

// FR-19, FR-20, NFR-09, [[IADR-0431]], [[IADR-0474]] (#1532): 利用者名簿の狭い読み口（`GetUserAttributes`）の偽物。
// 同期トークンの所有者の照会と退職の窓の照会の 2 つの gRPC 実装の試験が共用する。
internal sealed class FakeUserDirectoryClient : Pb.UserDirectory.UserDirectoryClient
{
    private readonly Pb.GetUserAttributesResponse? _answer;
    private readonly StatusCode? _failWith;
    private readonly bool? _hangAsRpc;

    private FakeUserDirectoryClient(Pb.GetUserAttributesResponse? answer, StatusCode? failWith, bool? hangAsRpc)
    {
        _answer = answer;
        _failWith = failWith;
        _hangAsRpc = hangAsRpc;
    }

    public string? LastUsername { get; private set; }

    public static FakeUserDirectoryClient Answering(Pb.GetUserAttributesResponse answer) => new(answer, null, null);

    public static FakeUserDirectoryClient Failing(StatusCode status) => new(null, status, null);

    public static FakeUserDirectoryClient Hanging(bool asRpcException) => new(null, null, asRpcException);

    public override AsyncUnaryCall<Pb.GetUserAttributesResponse> GetUserAttributesAsync(
        Pb.GetUserAttributesRequest request, CallOptions options)
    {
        LastUsername = request.Username;

        Task<Pb.GetUserAttributesResponse> response;
        if (_hangAsRpc is { } asRpc)
            response = HangAsync(asRpc, options.CancellationToken);
        else if (_failWith is { } status)
            response = Task.FromException<Pb.GetUserAttributesResponse>(
                new RpcException(new Status(status, "fake")));
        else
            response = Task.FromResult(_answer!);

        return new AsyncUnaryCall<Pb.GetUserAttributesResponse>(
            response, Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess, () => [], () => { });
    }

    // 取り消されるまで応答しない。Grpc.Net.Client の既定は取り消しを `RpcException(Cancelled)` で投げる。
    private static async Task<Pb.GetUserAttributesResponse> HangAsync(bool asRpc, CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) when (asRpc)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "fake"));
        }
        throw new InvalidOperationException("unreachable");
    }
}
