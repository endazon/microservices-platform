using DocumentService.Domain.Ports;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace DocumentService.Infrastructure.ExternalServices;

// FR-19, UC-11, SC-19, NFR-09, ADR-0029, ADR-0064, ADR-0075, ADR-0096 決定 1,
// [[IADR-0379]] 決定 4, [[IADR-0401]] 決定 2, [[IADR-0428]] 決定 3, [[IADR-0431]] (#1409):
// 退職の窓の照会の **east-west gRPC 実装**。
//
// 🔴 **新しい面は作っていない。** 既存の狭い読み口 `platform.authz.v1.UserDirectory/GetUserAttributes`
// の応答へ 2 項目（`enabled` / `retention_eligibility`）が足されたので、**それを読むだけ**である
// （[[IADR-0401]] 決定 2 の「列挙も書き込みも s2s の面に出さない」は保たれる）。
//
// 🔴 **利用者の `Authorization` を転送しない** —— 呼ぶのは定期処理であり、そもそも利用者が居ない。
// s2s の資格情報（`platform-service`）だけで呼ぶ（[[IADR-0379]] 決定 4）。
//
// ■ 縮退: `UserDirectoryGrpcClient` が `null` を返す（`RpcException` 全 status ／ s2s トークン取得失敗）。
//   そのまま `null` を通す —— **呼び出し元は削除しない側へ倒す。**
public sealed class GrpcOwnerRetentionDirectory(UserDirectoryGrpcClient client) : IOwnerRetentionDirectory
{
    public async Task<OwnerRetentionStatus?> GetAsync(string ownerId, CancellationToken ct)
    {
        var status = await client.GetRetentionStatusAsync(ownerId, ct);
        if (status is null) return null;

        // 🔴 **`Unspecified`（proto3 の既定 0）と未知の値は `NotEvaluable` へ倒す。**
        // 応答を返した相手が本項目を知らない古い配備でも、**既定値が削除を発火させない。**
        var eligibility = status.Eligibility switch
        {
            Pb.RetentionEligibility.Elapsed => OwnerRetentionEligibility.Elapsed,
            Pb.RetentionEligibility.WithinWindow => OwnerRetentionEligibility.WithinWindow,
            _ => OwnerRetentionEligibility.NotEvaluable,
        };
        return new OwnerRetentionStatus(status.Found, status.Enabled, eligibility);
    }
}

// FR-19, ADR-0096 決定 1, [[IADR-0431]] (#1409): 口が構成されていない配備の縮退。
//
// 🔴 **常に「引けなかった」を返す**（＝ 1 件も削除しない）。`Services:AuthorizationServiceGrpc` を
// 宣言していない配備で退職判定が走ることは無い —— **口の不在を「窓が閉じた」へ倒さない。**
// 空実装を置かずに例外を投げる形にすると、定期処理の他の 5 段（90 日の物理削除・版の刈り取り・
// 3 段通知）まで巻き添えで止まる。
public sealed class UnavailableOwnerRetentionDirectory : IOwnerRetentionDirectory
{
    public Task<OwnerRetentionStatus?> GetAsync(string ownerId, CancellationToken ct)
        => Task.FromResult<OwnerRetentionStatus?>(null);
}
