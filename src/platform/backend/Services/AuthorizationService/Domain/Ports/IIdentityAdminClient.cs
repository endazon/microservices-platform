namespace AuthorizationService.Domain.Ports;

// FR-05, FR-09, UC-05, SC-17, ADR-0026, IADR-0301: 身元プロバイダ（IdP）の管理操作の抽象。
//
// ■ なぜ抽象を切るのか
//   計画 05_screens §SC-17 は反映先を「Keycloak Admin API と属性ストア」と書くが、**利用者側の
//   ABAC 属性の実体は Keycloak のユーザー属性ひとつである**（realm の `abac-attributes` スコープが
//   `clearance` / `department` を user attribute → claim で写し、判定側 `BffScopeResolver.
//   ExtractUserAttributes` はそのクレームだけを読む）。認可サービス側に利用者の割当を持つ表は
//   存在せず、**作るべきでもない**（計画 06_technical/02_service-decomposition
//   「ID 管理を自作せず」）。したがって本サービスの責務は「IdP へ委譲すること」であり、
//   委譲先を差し替えられる形にしておく。
//
// ■ 🔴 **利用者の新規作成に相当する操作を持たない。**
//   計画 05_screens §SC-17 アクション:「アカウントは人事システム連携で自動プロビジョニングし、
//   退職者は連携により自動で無効化され全セッションが即時失効する（**本画面から新規作成はしない**）」。
//   規約で禁じるのではなく**型で持てなくする** —— 生やそうとした人がインターフェイスの改定に
//   ぶつかる。不在は `IdentityAdminContractTests` が反射で固定する。
//
// ■ 実装は 2 本（IADR-0301 決定 3）
//   `KeycloakIdentityAdminClient`（Admin REST）と `InMemoryIdentityAdminClient`（開発・テスト）。
//   どちらを起こすかは構成 `IdentityAdmin:Provider` の**明示的な宣言**で決まり、既定は無い。
public interface IIdentityAdminClient
{
    /// <summary>SC-17 主要素 1: 利用者を列挙する（ロール・ABAC 属性・状態つき）。</summary>
    Task<IReadOnlyList<IdentityUser>> ListUsersAsync(CancellationToken ct);

    /// <summary>
    /// SC-17 入力規則「定義済みロールのみ」の**値域の正**。IdP が持つ割当可能な realm ロールを返す。
    /// **画面にも後段にも焼き込まない** —— 焼き込むと realm を増やしても選べず、
    /// 消えたロールを選べてしまう。
    /// </summary>
    Task<IReadOnlyList<string>> ListAssignableRolesAsync(CancellationToken ct);

    /// <summary>
    /// SC-17: ABAC 属性の差し替え（部分更新ではない）。該当利用者が居なければ null。
    /// </summary>
    Task<IdentityUser?> ReplaceAttributesAsync(
        string userId, IReadOnlyDictionary<string, string> attributes, CancellationToken ct);

    /// <summary>
    /// SC-17: realm ロール割当の差し替え（併任可）。該当利用者が居なければ null。
    /// </summary>
    Task<IdentityUser?> ReplaceRealmRolesAsync(
        string userId, IReadOnlyList<string> roles, CancellationToken ct);

    /// <summary>SC-17: アカウントの有効／無効の切替。該当利用者が居なければ null。</summary>
    Task<IdentityUser?> SetEnabledAsync(string userId, bool enabled, CancellationToken ct);

    /// <summary>
    /// SC-17 アクション「無効化→全セッション即時失効」の後半。**その利用者の全セッションを失効させる。**
    /// Keycloak 側のセッション失効はバックチャネルログアウトを起こし、BFF の
    /// <c>BackchannelLogoutProcessor</c> が subject 単位でチケットを削除する（ADR-0032 / IADR-0273）。
    /// 戻り値は「失効を要求できたか」。
    /// </summary>
    Task<bool> RevokeSessionsAsync(string userId, CancellationToken ct);
}

// SC-17 主要素 1: IdP が持つ利用者の像。**本サービスはこれを永続化しない**（表を持たない）。
public sealed record IdentityUser(
    string Id,
    string Username,
    string DisplayName,
    bool Enabled,
    IReadOnlyList<string> Roles,
    IReadOnlyDictionary<string, string> Attributes);
