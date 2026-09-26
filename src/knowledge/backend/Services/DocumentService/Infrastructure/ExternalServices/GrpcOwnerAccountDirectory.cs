using DocumentService.Domain.Ports;
using Platform.Shared.Infrastructure.Foundation.Authz;

namespace DocumentService.Infrastructure.ExternalServices;

// FR-20, SC-17, NFR-09, NFR-14, ADR-0029, ADR-0075, 計画 ADR-0114 決定 1・2,
// [[IADR-0401]] 決定 2, [[IADR-0431]], [[IADR-0474]] (#1532):
// 同期トークンの所有者のアカウント状態の **east-west gRPC 実装**。
//
// 🔴 **新しい面は作っていない。** 退職の窓と同じ狭い読み口 `UserDirectory/GetUserAttributes` の
// `found` / `enabled` を読むだけである（利用者の `Authorization` は転送しない。s2s の資格情報で呼ぶ）。
//
// ■ 写像（通すのは Enabled だけ）
//   戻り値 null（`RpcException` 全 status ／ s2s トークン取得失敗）→ Unknown
//   found=false → NotFound、enabled=false → Disabled、それ以外 → Enabled
//   🔴 `enabled` を知らない古い認可サービスが応答すると proto3 の既定 false が返り Disabled になる ——
//   **配備の順序を誤ったときも通さない側へ倒れる**（ADR-0114 決定 2 と同じ向き）。
//
// ■ 時間切れ（ADR-0114 決定 2 が名指しした形）
//   チャネルに期限は無い。認可サービスが応答しないと、同期要求はその間止まり続ける。
//   🔴 **上限を掛けて Unknown へ倒す**（利用者を待たせたまま通すか通さないかを決めない状態を作らない）。
public sealed class GrpcOwnerAccountDirectory(UserDirectoryGrpcClient client) : IOwnerAccountDirectory
{
    // 同期 1 要求の中で名簿を待つ上限。プラグインの要求は利用者の操作・定期実行の双方から来るため、
    // 名簿の往復（同一クラスタ内の 1 往復）に対して十分長く、利用者が固まったと感じない長さに置く。
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _timeout = LookupTimeout;

    // 試験用: 時間切れの枝を短い上限で測る。
    internal GrpcOwnerAccountDirectory(UserDirectoryGrpcClient client, TimeSpan timeout) : this(client)
        => _timeout = timeout;

    public async Task<OwnerAccountState> GetStateAsync(string ownerId, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(_timeout);

        PlatformUserRetentionStatus? status;
        try
        {
            status = await client.GetAccountStatusAsync(ownerId, bounded.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 上限に達した（s2s トークン取得の途中など、gRPC の外で取り消された場合もここへ来る）。
            return OwnerAccountState.Unknown;
        }

        if (status is null)
        {
            // 🔴 本番のチャネルは取り消しを `RpcException(Cancelled)` で投げ（`ThrowOperationCanceledOnCancellation`
            //   は既定の false）、共有クライアントがそれを `null` に畳む。要求そのもの（`ct`）が取り消されて
            //   いたなら、時間切れ・障害と混ぜずに取り消しとして伝える（どちらでも応答は返らない）。
            ct.ThrowIfCancellationRequested();
            return OwnerAccountState.Unknown;
        }
        if (!status.Found) return OwnerAccountState.NotFound;
        return status.Enabled ? OwnerAccountState.Enabled : OwnerAccountState.Disabled;
    }
}

// FR-20, ADR-0114 決定 2, [[IADR-0474]] (#1532): 口が構成されていない配備の縮退。
//
// 🔴 **常に Unknown を返す（＝同期は 401）。** 名簿を引けない配備で同期を通すと、
// 無効化した利用者の持ち出しの経路が「構成の漏れ」で開く。例外を投げる形にしないのは、
// 同じ 401 で返す（deny-by-default・理由を漏らさない）ためである。
public sealed class UnavailableOwnerAccountDirectory : IOwnerAccountDirectory
{
    public Task<OwnerAccountState> GetStateAsync(string ownerId, CancellationToken ct)
        => Task.FromResult(OwnerAccountState.Unknown);
}
