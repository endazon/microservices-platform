namespace DataSourceService.Domain.Ports;

// FR-05, UC-04, SC-06, SC-17, ADR-0064 決定 4, ADR-0074 決定 4 (#1194): 基盤の利用者名簿の読み口。
//
// ■ なぜ SC-17 側のクライアントを使うのか
//   ADR-0074 決定 4 は「検証は ADR-0064 決定 4 が分けた**取り込み経路のクライアント**ではなく
//   **SC-17 側のクライアント（`view-users`）**で行う」と明記する。SC-17 の後段は
//   AuthorizationService（`IIdentityAdminClient` → 実 Keycloak。IADR-0329）であり、
//   **既にある口をそのまま使えば決定 4 を満たせる。**
//   したがって本ポートの実装は AuthorizationService への呼び出しであり、
//   `abac-seeder`（取り込み経路の主体）を使わない。
//
// ■ 🔴 **「居ない」と「引けなかった」を型で分ける**
//   空集合を返して済ませると、認可サービスが落ちている間の登録がすべて
//   「その利用者は存在しません」になる。**どちらも保存しない点では安全側だが、報告は嘘になる。**
//
// ■ 🔴 **口は「列挙」ではなく「照会」である**（#1255 / [[IADR-0401]] 決定 3）
//   従前は `ListUsernamesAsync(ct)`（名簿を全件引く）だった。全件列挙は AdminOnly であり、
//   REST 実装は**呼び出し元の利用者トークンを転送**して門を通していた —— east-west の面へ
//   利用者トークンを載せない規約（[[IADR-0379]] 決定 4）と正面から衝突する。
//   **本ポートが実際に要る問いは「これらの利用者名は実在するか」だけである。**
//   問いの形へ口を狭めたので、呼び出し先（`platform.authz.v1.UserDirectory/CheckUsernames`）も
//   狭いままでよく、サービス間の面へ名簿の列挙を出さずに済む。
//   REST 実装の挙動は変わらない（列挙して交差するだけ）。
public interface IPlatformUserDirectory
{
    /// <summary>
    /// 送った利用者識別子（`preferred_username`）のうち、基盤に**実在するもの**を返す。
    /// 名簿を引けなかったときは <see cref="PlatformUserDirectorySnapshot.Available"/> が false。
    /// <para>
    /// 🔴 照合は**序数一致**である（大小文字を畳まない）。無効化された利用者も実在として数える ——
    /// ADR-0074 決定 4 が課すのは「実在」であって「有効」ではない。
    /// </para>
    /// </summary>
    Task<PlatformUserDirectorySnapshot> LookupAsync(IReadOnlySet<string> usernames, CancellationToken ct);
}

// FR-05, SC-06 (#1194): 名簿の断面。**`Available=false` は「利用者が 0 人」ではない。**
//
// [[IADR-0401]] 決定 3 (#1255): 口が「照会」になったので、`Usernames` は
// **要求した名前のうち実在する部分集合**である（名簿の全件ではない）。
// `OwnerMappingTable.ValidateTargetsExist` の判定（`!knownUsernames.Contains(v)`）は
// どちらの意味でも同じ答えを返す —— 要求していない名前は判定に現れないからである。
public sealed record PlatformUserDirectorySnapshot(bool Available, IReadOnlySet<string> Usernames)
{
    public static PlatformUserDirectorySnapshot Unavailable { get; }
        = new(false, new HashSet<string>(StringComparer.Ordinal));

    public static PlatformUserDirectorySnapshot Of(IEnumerable<string> usernames)
        => new(true, new HashSet<string>(usernames, StringComparer.Ordinal));
}
