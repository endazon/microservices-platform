using DocumentService.Domain.Ports;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Infrastructure.ExternalServices;

// FR-05, FR-18, NFR-09, SC-05, ADR-0029, ADR-0075, 計画 ADR-0088 決定 1, ADR-0063 決定 3,
// [[IADR-0401]] 追記, [[IADR-0410]] 追記 2 (#1636): 承認者が管理者かの **east-west gRPC 実装**。
//
// 読むのは狭い読み口 `UserDirectory/CheckRealmRole`（名指しした 1 人が 1 つの realm ロールを実効で持つか）だけである
// （利用者の `Authorization` は転送しない。s2s の資格情報で呼ぶ）。
//
// ■ 写像
//   戻り値 null（`RpcException` 全 status ／ s2s トークン取得失敗）→ Unknown
//   true → Admin、false（持たない・居ない・無効化）→ NotAdmin
//   🔴 `CheckRealmRole` を知らない古い認可サービスは `UNIMPLEMENTED` を返す ＝ null ＝ Unknown —— **配備の順序を誤ったときも
//   管理者として通さない**（タグの反映は UNAVAILABLE → graph は 502。所有者の承認は影響を受けない）。
//
// ■ 時間切れ
//   チャネルに期限は無い（共有クライアントが 5 秒の deadline を掛ける）。s2s トークンの取得を含めて上限を掛け、Unknown へ倒す。
public sealed class GrpcApproverRoleDirectory(UserDirectoryGrpcClient client) : IApproverRoleDirectory
{
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    public async Task<ApproverAdminState> GetAdminStateAsync(string username, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(LookupTimeout);

        bool? hasRole;
        try
        {
            hasRole = await client.HasRealmRoleAsync(username, PlatformAuthPolicies.AdminRole, bounded.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApproverAdminState.Unknown;
        }

        if (hasRole is null)
        {
            // 要求そのものが取り消されていたなら、障害と混ぜずに取り消しとして伝える（`GrpcOwnerAccountDirectory` と同じ）。
            ct.ThrowIfCancellationRequested();
            return ApproverAdminState.Unknown;
        }
        return hasRole.Value ? ApproverAdminState.Admin : ApproverAdminState.NotAdmin;
    }
}

// FR-18, NFR-09, [[IADR-0410]] 追記 2 (#1636): 口が構成されていない配備の縮退。
// 🔴 **常に Unknown を返す** —— 名簿を引けない配備で本文のロールへ戻ると、中継者の主張で管理者の上書きが開く。
// 所有者の承認は影響を受けない（管理者の判定は所有者の枝で書けないときだけ呼ばれる）。
public sealed class UnavailableApproverRoleDirectory : IApproverRoleDirectory
{
    public Task<ApproverAdminState> GetAdminStateAsync(string username, CancellationToken ct)
        => Task.FromResult(ApproverAdminState.Unknown);
}
