namespace DataSourceService.Domain.Ports;

// FR-05, UC-04, SC-06, SC-17, ADR-0064 決定 4, ADR-0074 決定 4 (#1194): 基盤の利用者名簿の読み口。
//
// ■ なぜ SC-17 側のクライアントを使うのか
//   ADR-0074 決定 4 は「検証は ADR-0064 決定 4 が分けた**取り込み経路のクライアント**ではなく
//   **SC-17 側のクライアント（`view-users`）**で行う」と明記する。SC-17 の後段は
//   AuthorizationService の `GET /authz/users`（`IIdentityAdminClient` → 実 Keycloak。IADR-0329）であり、
//   **既にある口をそのまま使えば決定 4 を満たせる。**
//   したがって本ポートの実装は AuthorizationService への HTTP 呼び出しであり、
//   `abac-seeder`（取り込み経路の主体）を使わない。
//
// ■ 🔴 **「居ない」と「引けなかった」を型で分ける**
//   空集合を返して済ませると、認可サービスが落ちている間の登録がすべて
//   「その利用者は存在しません」になる。**どちらも保存しない点では安全側だが、報告は嘘になる。**
public interface IPlatformUserDirectory
{
    /// <summary>
    /// 基盤に実在する利用者識別子（`preferred_username`）の集合を返す。
    /// 名簿を引けなかったときは <see cref="PlatformUserDirectorySnapshot.Available"/> が false。
    /// </summary>
    Task<PlatformUserDirectorySnapshot> ListUsernamesAsync(CancellationToken ct);
}

// FR-05, SC-06 (#1194): 名簿の断面。**`Available=false` は「利用者が 0 人」ではない。**
public sealed record PlatformUserDirectorySnapshot(bool Available, IReadOnlySet<string> Usernames)
{
    public static PlatformUserDirectorySnapshot Unavailable { get; }
        = new(false, new HashSet<string>(StringComparer.Ordinal));

    public static PlatformUserDirectorySnapshot Of(IEnumerable<string> usernames)
        => new(true, new HashSet<string>(usernames, StringComparer.Ordinal));
}
