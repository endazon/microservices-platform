using DocumentService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Authz;

namespace DocumentService.Infrastructure.ExternalServices;

// FR-05, FR-19, NFR-09, ADR-0029, ADR-0075, 計画 ADR-0119 決定 3, ADR-0088 決定 1 (#1614):
// 読み取りの許可の **east-west gRPC 実装**（`platform.authz.v1.AuthzScope/Resolve`、action=read）。
//
// 🔴 **新しい面は作っていない。** BFF・検索・グラフが使っている同じ口・同じ共有クライアントである。
// 利用者の `Authorization` は転送しない（s2s の資格情報で呼び、利用者は本文の `user_id` で名指す）。
// 利用者属性は送らない —— 認可サービスは本文の属性を信じず IdP から引き直す（[[IADR-0416]]）。
//
// ■ 縮退（すべて null ＝ 読めない）
//   許可なし（granted=false）・`RpcException` 全 status・s2s トークン取得失敗（共有クライアントが null に畳む）・
//   上限の時間切れ。🔴 **要求そのものが取り消されたときだけは取り消しとして伝える**（応答は返らない）。
public sealed class GrpcDocumentReadScopeSource(AuthzScopeGrpcClient client) : IDocumentReadScopeSource
{
    // 読み取り 1 要求の中で認可サービスを待つ上限（`GrpcOwnerAccountDirectory` と同じ桁）。
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private static readonly IReadOnlyDictionary<string, string> NoAttributes = new Dictionary<string, string>();

    public async Task<IReadOnlyList<IReadOnlyList<AttributeFilter>>?> ResolveReadBranchesAsync(
        string userId, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(LookupTimeout);

        BffAccessScope? scope;
        try
        {
            scope = await client.ResolveAsync(userId, NoAttributes, BffScopeAction.Read, bounded.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        if (scope is null)
        {
            // 共有クライアントは取り消しも `RpcException(Cancelled)` として null に畳む。
            ct.ThrowIfCancellationRequested();
            return null;
        }

        return ToBranches(scope);
    }

    // 分岐があれば分岐の並び、無ければ従来の連言 1 つ（BFF の `IsReadable` と同じ読み方）。
    internal static IReadOnlyList<IReadOnlyList<AttributeFilter>>? ToBranches(BffAccessScope scope)
    {
        if (!scope.GrantsAccess) return null;
        return scope.Branches is { Count: > 0 }
            ? scope.Branches.Select(b => (IReadOnlyList<AttributeFilter>)b.Filters).ToList()
            : [scope.Filters];
    }
}

// FR-19, 計画 ADR-0119 決定 3 (#1614): `Services:AuthorizationServiceGrpc` が未構成の配備の縮退。
// 🔴 **常に「読めるものは無い」**（グループの共有先の個人資料は誰にも返らない。所有者・利用者の共有先・
// 組織文書の読み取りは影響を受けない）。口の不在を「許可」へ倒さない。
public sealed class UnavailableDocumentReadScopeSource : IDocumentReadScopeSource
{
    public Task<IReadOnlyList<IReadOnlyList<AttributeFilter>>?> ResolveReadBranchesAsync(
        string userId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<IReadOnlyList<AttributeFilter>>?>(null);
}
