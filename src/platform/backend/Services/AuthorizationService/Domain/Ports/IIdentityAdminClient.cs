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
    /// FR-05, FR-16, NFR-09, UC-09, SC-12, 計画 ADR-0088 決定 1・3, ADR-0062 決定 3,
    /// [[IADR-0413]] (#1333): **利用者名で 1 人だけを引く。** 居なければ null。
    ///
    /// 🔴 **列挙の上で絞る形を置き換えるためにある。** 従前、名指しの 1 人が要る経路
    /// （ABAC 判定の属性の引き直し・`UserDirectory/GetUserAttributes`）は
    /// <see cref="ListUsersAsync"/> の結果を絞っていた。それには 2 つの欠陥があった ——
    /// <list type="number">
    /// <item>判定 1 回ごとに**全利用者の列挙 ＋ 人数分のロール照会**が走る</item>
    /// <item>🔴 列挙は **1000 件で打ち切られる**ので、**1001 人目以降は「居ない」に見える**。
    /// `ADR-0088` 決定 1 の下でそれは deny であり、**実在する利用者が人数の増加だけで締め出される**</item>
    /// </list>
    /// 🔴 **したがって「1 人だけ要るときは列挙しない」は最適化ではなく正しさである。**
    ///
    /// 🔴 **これは新規作成の口ではない**（`IdentityAdminContractTests` の禁止語に触れない読み取りである）。
    /// </summary>
    Task<IdentityUser?> FindByUsernameAsync(string username, CancellationToken ct);

    /// <summary>
    /// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1, [[IADR-0445]] (#1445):
    /// **共有先に指定する利用者を名前で探す**（部分一致・**有効な利用者だけ**・最大 <paramref name="max"/> 件）。
    ///
    /// 🔴 **これは新規作成の口ではない**（`IdentityAdminContractTests` の禁止語に触れない読み取りである）。
    ///
    /// 🔴 **<see cref="ListUsersAsync"/> で代用できない。** あちらは AdminOnly の管理面が使う
    /// 全件列挙であり、ロール・ABAC 属性つきの広い像を返す。共有先の選択は**一般利用者の操作**で
    /// あって、面に出してよいのは利用者名・表示名・有効状態の 3 つだけである（決定 1
    /// 「画面には表示名を出し、識別子は出さない」）。列挙を一般利用者へ開くと、**全社の名簿と
    /// 属性が誰からでも引ける**ことになる。
    ///
    /// 🔴 **ロールは引かない**（<see cref="FindByUsernameAsync"/> と同じ判断）。`Roles` が空なのは
    /// 「ロールが無い」ではなく「この口では引いていない」である —— 呼び出し元（共有先の検索）は
    /// ロールを読まず、引くと 1 人あたり往復が 1 つ増える。
    ///
    /// 🔴 **無効化済み（退職者）は返さない。** 退職者を新たな共有先に指定できてはならない。
    /// 既存の共有先の**表示**は <see cref="FindByUsernameAsync"/> 側で引く（そちらは無効化済みも返る）。
    /// </summary>
    Task<IReadOnlyList<IdentityUser>> SearchUsersAsync(string query, int max, CancellationToken ct);

    /// <summary>
    /// FR-05, FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0036 D-03・D-06, ADR-0088 決定 1,
    /// ADR-0098 決定 1, [[IADR-0447]] (#1447): **利用者の所属グループを引く**
    /// （`${current_groups}` の供給元）。<paramref name="userId"/> は **IdP の内部 ID**
    /// （<see cref="IdentityUser.Id"/>）であって利用者名ではない。
    ///
    /// 🔴 **トークンの `groups` クレームを読む形は採らない。** `/authz/scope` は本文もトークンも
    /// 信じず属性を IdP から引き直す（`ADR-0088` 決定 1）——**同じ点で所属も引く**のでなければ、
    /// 属性だけが引き直され所属は呼び出し元の主張のままになる（片方だけ信じる形が一番危ない）。
    ///
    /// 🔴 **返すのは ID・名前・パスの 3 つだけである**（<see cref="IdentityGroup"/>）。
    /// 判定に使うのは `Id`（`ADR-0098` 決定 1「共有先は識別子」。改名・移動で共有が外れない）で、
    /// 名前とパスは画面の表示用である。属性・所属者は運ばない。
    ///
    /// 🔴 **これは新規作成の口ではない**（`IdentityAdminContractTests` の禁止語に触れない読み取りである）。
    /// </summary>
    Task<IReadOnlyList<IdentityGroup>> GetUserGroupsAsync(string userId, CancellationToken ct);

    /// <summary>
    /// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1・3, [[IADR-0447]] (#1447):
    /// **共有先に指定するグループを名前で探す**（部分一致・大小文字無視・最大 <paramref name="max"/> 件・パス順）。
    ///
    /// 🔴 **グループ木を平坦化して返す。** 計画 `ADR-0098` 決定 3 はグループ木を管理者が Keycloak で
    /// 作ると定めており、木の形（親子）は共有の単位ではない —— 指定できるのは**個々のグループ**である。
    /// 階層は <see cref="IdentityGroup.Path"/> が表し、画面は同名グループの区別にそれを使う。
    ///
    /// 🔴 **これは新規作成の口ではない**（禁止語に触れない読み取りである）。
    /// </summary>
    Task<IReadOnlyList<IdentityGroup>> SearchGroupsAsync(string query, int max, CancellationToken ct);

    /// <summary>
    /// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1, [[IADR-0447]] (#1447):
    /// **グループ ID の集合を像へ引く**（共有台帳の `subjectId` → 画面に出す表示名）。
    ///
    /// 🔴 **無い ID は応答から落ちる。エラーではない。** 共有台帳は IdP の外にあり、台帳に残った
    /// グループが Keycloak から消えていることは起こり得る（削除・realm の入れ替え）。
    /// 落とさずに失敗させると、**画面が 1 件の不整合で共有先を 1 つも表示できなくなる** ——
    /// 取り消すべき相手が見えないのが最悪である（`ResolveUsersEndpoint` と同じ判断）。
    ///
    /// 🔴 **これは新規作成の口ではない**（禁止語に触れない読み取りである）。
    /// </summary>
    Task<IReadOnlyList<IdentityGroup>> GetGroupsByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>
    /// SC-17 入力規則「定義済みロールのみ」の**値域の正**。IdP が持つ割当可能な realm ロールを返す。
    /// **画面にも後段にも焼き込まない** —— 焼き込むと realm を増やしても選べず、
    /// 消えたロールを選べてしまう。
    /// </summary>
    Task<IReadOnlyList<string>> ListAssignableRolesAsync(CancellationToken ct);

    /// <summary>
    /// SC-17: ABAC 属性の差し替え（部分更新ではない）。該当利用者が居なければ null。
    ///
    /// 🔴 **予約キー（保持起点。<see cref="AuthorizationService.Domain.RetentionAnchorAttributes"/>）だけは
    /// 差し替えの対象外であり、現在値を持ち越す**（[[IADR-0428]] / #1392）。予約キーは ABAC 属性では
    /// なく、画面が送る差し替え要求にも含まれない —— **持ち越さないと、部門を 1 つ直しただけで
    /// 退職時の窓の起点が黙って消える。** 要求側に予約キーが混ざっていても採らない。
    /// </summary>
    Task<IdentityUser?> ReplaceAttributesAsync(
        string userId, IReadOnlyDictionary<string, string> attributes, CancellationToken ct);

    /// <summary>
    /// FR-19, SC-17, SC-19, 計画 ADR-0036 D-09, ADR-0082 決定 5, [[IADR-0428]] (#1392):
    /// **退職時の 30 日窓の起点（保持起点）を書く唯一の口。** 該当利用者が居なければ null。
    ///
    /// <paramref name="anchorAt"/> が null なら起点を**消す**（再有効化＝退職の取り消し）。
    ///
    /// 🔴 **差し替え（<see cref="ReplaceAttributesAsync"/>）と兼用にできない。** あちらは予約キーを
    /// **保存する**意味論であり、消去を表せない。**起点の書き手を 1 つに閉じる**ことで、
    /// 「誰がいつ起点を動かしたか」が型のうえで 1 か所に収まる。
    ///
    /// 🔴 **これは新規作成の口ではない**（`IdentityAdminContractTests` の禁止語に触れない属性の書き込みである）。
    /// </summary>
    Task<IdentityUser?> SetRetentionAnchorAsync(
        string userId, string attributeKey, DateTimeOffset? anchorAt, CancellationToken ct);

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

// FR-05, FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0036 D-03・D-06, ADR-0098 決定 1・3,
// [[IADR-0447]] (#1447): IdP が持つグループの像。**本サービスはこれを永続化しない**
// （グループ木の正本は Keycloak であり、決定 3 が「管理者が Keycloak で作る」と定めている）。
//
// 🔴 **`Id` が共有の鍵である。** 共有台帳のグループ `subjectId`・`${current_groups}` の束縛値・
// 画面が取り消しに使う値がすべてこの ID で揃う（`ADR-0098` 決定 1）。
// `Name` は表示名、`Path` は同名グループを区別するための階層（例 `/teams/knowledge`）である。
//
// 🔴 **所属者・属性を持たない。** 一般利用者へ開く面（`GroupSummaryDto` の 3 項目）より
// 広い像を作らない —— 広い像を作ると、面を絞る責務が端点側の「書き忘れ」になる。
public sealed record IdentityGroup(string Id, string Name, string Path);
