using DocumentService.Domain.Ports;
using Grpc.Core;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace DocumentService.Infrastructure.ExternalServices;

// FR-05, FR-19, NFR-09, ADR-0029, ADR-0075, 計画 ADR-0121 決定 2, [[IADR-0379]], [[IADR-0481]] (#1665):
// 所有者の読み取りのポリシーの件数の **east-west gRPC 実装**（`platform.authz.v1.AuthzScope/GetOwnerReadPolicyStatus`）。
//
// 🔴 **新しいチャネル・主体は作っていない。** 読み取りのスコープ（`GrpcDocumentReadScopeSource`）と同じ生成クライアント・
// 同じ共有チャネル・同じ s2s の資格情報である（`AddAuthzScopeGrpcClient` が登録する）。
//
// ■ 縮退（すべて null ＝ 数えられない ＝ 門は開かない）
//   `RpcException` 全 status（古い認可サービスの UNIMPLEMENTED を含む）・s2s トークン取得失敗・上限の時間切れ。
//   🔴 **呼び出し元の取り消しだけは取り消しとして伝える**（常駐の停止を「数えられない」と記録しない）。
public sealed class GrpcOwnerReadPolicyStatusSource(
    Pb.AuthzScope.AuthzScopeClient client,
    ILogger<GrpcOwnerReadPolicyStatusSource> logger) : IOwnerReadPolicyStatusSource
{
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _timeout = LookupTimeout;

    // 試験用: 時間切れの枝を短い上限で測る。
    internal GrpcOwnerReadPolicyStatusSource(
        Pb.AuthzScope.AuthzScopeClient client, ILogger<GrpcOwnerReadPolicyStatusSource> logger, TimeSpan timeout)
        : this(client, logger)
        => _timeout = timeout;

    public async Task<int?> GetActiveCountAsync(CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(_timeout);
        try
        {
            var resp = await client.GetOwnerReadPolicyStatusAsync(
                new Pb.GetOwnerReadPolicyStatusRequest(), cancellationToken: bounded.Token);
            return resp.ActiveCount;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException
                                   || (ex is RpcException rpc && bounded.IsCancellationRequested
                                       && rpc.StatusCode == StatusCode.Cancelled))
        {
            logger.LogWarning(
                "所有者の読み取りのポリシーの件数を {Timeout} 秒以内に引けなかった。数えられないとして扱う（門は開かない）。",
                _timeout.TotalSeconds);
            return null;
        }
        catch (RpcException ex)
        {
            logger.LogWarning(
                "所有者の読み取りのポリシーの件数を引けなかった（{Status}）。数えられないとして扱う（門は開かない）。", ex.StatusCode);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "s2s トークンが取得できないため所有者の読み取りのポリシーの件数を引けない。数えられないとして扱う。");
            return null;
        }
    }
}

// `Services:AuthorizationServiceGrpc` が未構成の配備の縮退。🔴 **常に「数えられない」**（門は開かない）。
public sealed class UnavailableOwnerReadPolicyStatusSource : IOwnerReadPolicyStatusSource
{
    public Task<int?> GetActiveCountAsync(CancellationToken ct) => Task.FromResult<int?>(null);
}
