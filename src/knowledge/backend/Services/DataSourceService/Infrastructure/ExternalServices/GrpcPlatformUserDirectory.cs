using DataSourceService.Domain.Ports;
using Platform.Shared.Infrastructure.Foundation.Authz;

namespace DataSourceService.Infrastructure.ExternalServices;

// FR-05, UC-04, SC-06, SC-17, NFR-09, NFR-16, ADR-0029, ADR-0064 決定 4, ADR-0074 決定 4, ADR-0075,
// [[IADR-0329]], [[IADR-0379]], [[IADR-0401]] 決定 2・3 (#1255):
// 写像先の実在検証の **east-west gRPC 実装**（兄弟クラス。REST 実装は `AuthorizationServiceUserDirectory`）。
//
// 🔴 **利用者の `Authorization` を転送しない。** REST 実装は `/authz/users`（AdminOnly・全件列挙）を
// 通すために転送していたが、east-west の面へ利用者トークンを載せると呼び出し先が
// 「利用者が直接呼んだ」と区別できず confused deputy になる（[[IADR-0379]] 決定 4 /
// `docs/api/east-west-grpc.md` §4）。
//
// **代わりに呼び出し先の読み口を狭めた**（[[IADR-0401]] 決定 2）——
// `platform.authz.v1.UserDirectory/CheckUsernames` は「送った名前が実在するか」しか答えず、
// **列挙も書き込みも s2s の面に存在しない**。したがってサービス専用の資格情報
// （`platform-service`）を新設しても、SC-06 を触れない主体が**名簿を引ける経路はできない** ——
// REST 実装の注記が守ろうとした線はここで保たれている。
// 人の側の門（Create / Update / Patch / Disable の `AdminOnly`）は本サービスの端点に残る。
//
// ■ 縮退（REST 実装と同じ枝）
//   `RpcException`（全 status）と s2s トークン取得失敗はいずれも `Unavailable`（＝「引けなかった」）。
//   **「実在しない」と混ぜない** —— 呼び出し元はこれを 502 へ、実在しないは 400 へ写す
//   （`OwnerMappingValidation`）。混ぜると認可サービスの障害が「その利用者は存在しません」という
//   嘘の理由になる。判定は `UserDirectoryGrpcClient` が `null` / 非 null で表す。
public sealed class GrpcPlatformUserDirectory(UserDirectoryGrpcClient client) : IPlatformUserDirectory
{
    public async Task<PlatformUserDirectorySnapshot> LookupAsync(
        IReadOnlySet<string> usernames, CancellationToken ct)
    {
        // 空集合は呼び出し元（`OwnerMappingValidation`）が先に弾いているが、
        // **口としては空でも呼べる**ようにしておく（後段へ空の要求を投げない）。
        if (usernames.Count == 0) return PlatformUserDirectorySnapshot.Of([]);

        var existing = await client.CheckUsernamesAsync([.. usernames], ct);
        return existing is null
            ? PlatformUserDirectorySnapshot.Unavailable
            : PlatformUserDirectorySnapshot.Of(existing);
    }
}
