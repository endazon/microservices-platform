using DataSourceService.Domain.Ports;
using Platform.Shared.Infrastructure.Foundation.Authz;

namespace DataSourceService.Infrastructure.ExternalServices;

// FR-05, UC-04, SC-06, NFR-09, NFR-16, ADR-0029, ADR-0064 決定 4, ADR-0074 決定 4, ADR-0075,
// 計画 ADR-0115 決定 1・5, [[IADR-0379]] 決定 4, [[IADR-0401]] 決定 2・3, [[IADR-0472]] (#1557):
// 部門コードの値域照会の **east-west gRPC 実装**（`platform.authz.v1.UserDirectory/CheckDepartmentCodes`）。
//
// 🔴 **本サービスは Keycloak を直接叩かない。** 後段は AuthorizationService の `IIdentityAdminClient`
// （`identity-admin`）であり、realm を読む主体は 1 つのまま（ADR-0074 決定 4 が名指しした SC-17 側のクライアント）。
// 🔴 **利用者の `Authorization` を転送しない**（s2s の `platform-service` だけで呼ぶ。`GrpcPlatformUserDirectory` と同じ）。
//
// ■ 縮退: `UserDirectoryGrpcClient` が `null`（`RpcException` 全 status ／ s2s トークン取得失敗）なら `Unavailable`。
//   呼び出し元（`DepartmentDomainValidation`）はそれを 502 へ、値域の外を 400 へ写す。
public sealed class GrpcDepartmentDomainDirectory(UserDirectoryGrpcClient client) : IDepartmentDomainDirectory
{
    public async Task<DepartmentDomainSnapshot> LookupAsync(IReadOnlySet<string> codes, CancellationToken ct)
    {
        // 空集合は呼び出し元が先に弾いているが、口としては空でも呼べる（後段へ空の要求を投げない）。
        if (codes.Count == 0) return DepartmentDomainSnapshot.Of([]);

        var existing = await client.CheckDepartmentCodesAsync([.. codes], ct);
        return existing is null
            ? DepartmentDomainSnapshot.Unavailable
            : DepartmentDomainSnapshot.Of(existing);
    }
}

// FR-05, SC-06, [[IADR-0472]] 決定 2 (#1557): gRPC 宛先（`Services:AuthorizationServiceGrpc`）を宣言していない配備の縮退。
//
// 🔴 **常に「引けなかった」を返す**（＝明示した部門は 502 で保存されない）。**口の不在を「値域の内」へも「値域の外」へも
// 倒さない** —— 前者は検証を黙って外し、後者は存在しない理由で拒否する（原則 A）。
// 予約値・空白・未指定の部門は照会しないので、この縮退の下でも登録・更新そのものは通る。
// REST の兄弟実装は作らない —— 利用者トークンを転送して `/authz/groups/lookup` を引く形へ戻ることになる
// （[[IADR-0401]] 決定 2 と逆向き）。形は [[IADR-0431]] の `UnavailableOwnerRetentionDirectory` と同じ。
public sealed class UnavailableDepartmentDomainDirectory : IDepartmentDomainDirectory
{
    public Task<DepartmentDomainSnapshot> LookupAsync(IReadOnlySet<string> codes, CancellationToken ct)
        => Task.FromResult(DepartmentDomainSnapshot.Unavailable);
}
