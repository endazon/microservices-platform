using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace RetrievalService.Tests.Infrastructure.ExternalServices;

// FR-05, NFR-09, ADR-0029, ADR-0075, [[IADR-0379]], [[IADR-0401]], [[IADR-0416]] (#1255 / #1339):
//
// ★ 本ファイルは `WikiService.Tests` / `GraphService.Tests` の同名の器と**同じ内容の 3 本目**である。
//   試験用の替え玉であり規則ではないので写しを許すが、**4 本目を作る前に共有の試験支援
//   プロジェクトを立てること**（[[IADR-0141]] の「同型 2 回で条件を満たす」に照らせば既に超えている。
//   本 PR の射程外なので記録に留める）。
//
// east-west gRPC の**生成クライアントの偽物**。生成された `AuthzScope.AuthzScopeClient` は
// 仮想メソッドと protected な既定コンストラクタを持つので、実チャネル無しで差し替えられる。
//
// 🔴 **応答は REST 側と同じ入力（契約 DTO）から組む。** 手で proto を組むと、REST と gRPC の
// 同値試験が「同じ値を 2 回書いた」だけになり写像の欠陥を検出できない（#1295 の変異検査が
// 実際に見つけた穴）。ここでは呼び出し先 `AuthzScopeGrpcService` が行う写しと同じ形を通す。
//
// 🔴 **呼び出し回数を数える。** 「呼ばない」ことは回数でしか表明できない。
internal sealed class FakeAuthzScopeClient : Pb.AuthzScope.AuthzScopeClient
{
    private readonly Pb.ResolveScopeResponse? _response;
    private readonly StatusCode? _failWith;

    private FakeAuthzScopeClient(Pb.ResolveScopeResponse? response, StatusCode? failWith)
    {
        _response = response;
        _failWith = failWith;
    }

    public Pb.ResolveScopeRequest? LastRequest { get; private set; }

    public int CallCount { get; private set; }

    public static FakeAuthzScopeClient Returning(AccessScopeResponse scope) => new(ToProto(scope), null);

    public static FakeAuthzScopeClient Failing(StatusCode status) => new(null, status);

    public override AsyncUnaryCall<Pb.ResolveScopeResponse> ResolveAsync(
        Pb.ResolveScopeRequest request, CallOptions options)
    {
        CallCount++;
        LastRequest = request;
        if (_failWith is { } status)
        {
            var ex = new RpcException(new Status(status, "fake"));
            return new AsyncUnaryCall<Pb.ResolveScopeResponse>(
                Task.FromException<Pb.ResolveScopeResponse>(ex), Task.FromResult(new Metadata()),
                () => new Status(status, "fake"), () => [], () => { });
        }

        return new AsyncUnaryCall<Pb.ResolveScopeResponse>(
            Task.FromResult(_response!), Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess, () => [], () => { });
    }

    /// <summary>契約 DTO → proto（呼び出し先 `AuthzScopeGrpcService` の写しと同じ形）。</summary>
    internal static Pb.ResolveScopeResponse ToProto(AccessScopeResponse scope)
    {
        var proto = new Pb.ResolveScopeResponse { UserId = scope.UserId, Granted = scope.Granted };
        proto.AllowedFilters.AddRange(scope.AllowedFilters.Select(ToProto));
        if (scope.Branches is { Count: > 0 })
            proto.Branches.AddRange(scope.Branches.Select(b =>
            {
                var branch = new Pb.AccessScopeBranch { Name = b.Name };
                branch.Filters.AddRange(b.Filters.Select(ToProto));
                return branch;
            }));
        return proto;
    }

    private static Pb.AttributeFilter ToProto(AttributeFilter f)
    {
        var proto = new Pb.AttributeFilter { Key = f.Key };
        proto.AllowedValues.AddRange(f.AllowedValues);
        return proto;
    }

    /// <summary>偽の生成クライアントを本物のラッパへ差し込む。</summary>
    public AuthzScopeGrpcClient Wrap() => new(this, NullLogger<AuthzScopeGrpcClient>.Instance);
}
