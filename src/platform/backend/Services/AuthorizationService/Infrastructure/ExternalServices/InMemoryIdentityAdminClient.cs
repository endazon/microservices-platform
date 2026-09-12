using AuthorizationService.Domain;
using AuthorizationService.Domain.Ports;
using System.Collections.Concurrent;

namespace AuthorizationService.Infrastructure.ExternalServices;

// FR-05, FR-09, SC-17, IADR-0301 決定 3: 開発・テスト用の身元プロバイダ。
//
// 🔴 **実 IdP へは反映されない。** 選ばれたことを起動時に警告ログで 1 行出す ——
// 黙って動くと「保存できたのに認可が変わらない」を誰も追えない。
//
// 初期データは realm export（`deploy/keycloak/microservices-platform-realm.json`）と
// 計画 05_screens §SC-17 のモックアップの両方に似せてある。**属性の値は
// `deploy/local/abac-seed/attributes.json` の利用者スコープ許可値から採っている**ので、
// 辞書を投入した開発環境でそのまま保存が通る。
public sealed class InMemoryIdentityAdminClient : IIdentityAdminClient
{
    // 実 realm が持つ 2 ロール（`platform-admin` / `platform-operator`）に、Keycloak 既定の
    // 合成ロール（`default-roles-*` / `offline_access` / `uma_authorization`）を混ぜない。
    // **割当可能ロールの値域は「人が割り当てる realm ロール」だけ**である。
    private static readonly string[] AssignableRoles =
        ["platform-admin", "platform-operator", "wiki-editor"];

    private readonly ConcurrentDictionary<string, MutableUser> _users = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _revoked = new();

    // FR-05, FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1・3, [[IADR-0447]] (#1447):
    // **固定のグループ木**（`${current_groups}` の束縛と共有先の検索を開発環境で通すための最小の木）。
    //
    // 🔴 **realm export（`deploy/keycloak/microservices-platform-realm.json`）へは足さない** ——
    // 計画 `ADR-0098` 決定 3 が「グループ木は管理者が Keycloak で作る」と定めており、
    // 例示の木を配備の正本へ焼き込むと**管理者が作る前提の構造を実装が先取りする**ことになる。
    // 開発・テストだけがこの偽物の木を見る。
    //
    // 🔴 **親も 1 つの群として並ぶ**（`/teams` も指定できる）。本物（Keycloak）の平坦化が
    // 祖先を落とさないため、ここだけ子だけを持つと**偽物で緑になる試験が本物では別の答えを返す**。
    private static readonly IdentityGroup[] Groups =
    [
        new("g-teams", "teams", "/teams"),
        new("g-knowledge", "knowledge", "/teams/knowledge"),
        new("g-finance", "finance", "/teams/finance"),
        new("g-department", "department", "/department"),
        new("g-engineering", "engineering", "/department/engineering"),
    ];

    // 利用者の**内部 ID** → 所属グループ ID（`GetUserGroupsAsync` の鍵は利用者名ではない）。
    // 既存 seed の 4 人に 1〜2 件ずつ与える。**属性（department）と重ねてあるが別物である** ——
    // 属性は ABAC の条件、グループは共有先の識別子である。
    private static readonly Dictionary<string, string[]> Memberships = new(StringComparer.Ordinal)
    {
        ["u-tanaka"] = ["g-finance"],
        ["u-sato"] = ["g-knowledge", "g-engineering"],
        ["u-suzuki"] = ["g-knowledge"],
        ["u-takahashi"] = ["g-teams"],
    };

    /// <summary>
    /// 失効を要求された利用者 ID（要求された順）。
    ///
    /// 🔴 **観測点として置いてある。** セッションの実体は BFF 側のチケットストアであり、この偽物は
    /// 持たない。しかし計画 05_screens §SC-17 の「無効化→**全セッション即時失効**」は、
    /// 「無効化した」だけでは満たされない —— **失効の要求が出たことを測れないと、失効を落とす変異が
    /// 素通りする**（実測済み）。テストはここを見る。
    /// </summary>
    public IReadOnlyList<string> RevokedSessionRequests => [.. _revoked];

    public InMemoryIdentityAdminClient(ILogger<InMemoryIdentityAdminClient> logger)
    {
        logger.LogWarning(
            "IdentityAdmin:Provider=in-memory で起動した。利用者アカウント管理（SC-17）の変更は "
            + "プロセス内にしか残らず、実 IdP（Keycloak）へは反映されない。"
            + "本番では IdentityAdmin:Provider=keycloak を注入すること。");

        Seed("u-tanaka", "tanaka.taro", "田中 太郎", true,
            ["platform-operator"], new() { ["department"] = "finance", ["clearance"] = "internal" });
        Seed("u-sato", "sato.hanako", "佐藤 花子", true,
            ["platform-admin"], new() { ["department"] = "engineering", ["clearance"] = "restricted" });
        Seed("u-suzuki", "suzuki.ichiro", "鈴木 一郎", true,
            ["platform-admin", "platform-operator"],
            new() { ["department"] = "sales", ["clearance"] = "confidential" });
        // 退職者。人事連携で自動的に無効化され、全セッションが失効した状態を表す
        // （計画 05_screens §SC-17 アクション）。**画面から作られたのではない。**
        Seed("u-takahashi", "takahashi.jiro", "高橋 次郎", false,
            ["platform-operator"], new() { ["department"] = "hr", ["clearance"] = "public" });
    }

    private void Seed(string id, string username, string displayName, bool enabled,
        List<string> roles, Dictionary<string, string> attributes)
        => _users[id] = new MutableUser
        {
            Id = id,
            Username = username,
            DisplayName = displayName,
            Enabled = enabled,
            Roles = roles,
            Attributes = attributes,
        };

    public Task<IReadOnlyList<IdentityUser>> ListUsersAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<IdentityUser>>(
            [.. _users.Values.OrderBy(u => u.Username, StringComparer.Ordinal).Select(u => u.ToIdentityUser())]);

    // FR-05, FR-16, NFR-09, SC-12, 計画 ADR-0088 決定 1, [[IADR-0413]] (#1333): 名指しの 1 人。
    // 🔴 **照合は大小文字無視**（Keycloak 実装・`GetUserAttributes` の現行と同じ規則）。
    // 🔴 **ロールも返す** —— 偽物には往復の費用が無く、**本物より狭い像を返す理由が無い**
    // （呼び出し元はロールを読まないので、どちらでも判定は変わらない）。
    public Task<IdentityUser?> FindByUsernameAsync(string username, CancellationToken ct)
        => Task.FromResult(_users.Values
            .FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase))
            ?.ToIdentityUser());

    // FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1, [[IADR-0445]] (#1445): 共有先の候補。
    // 🔴 **本物（Keycloak の `search=`）と同じ意味論にする** —— 利用者名・表示名の部分一致
    // （大小文字無視）・**有効な利用者だけ**・表示名順・`max` 件。ここだけ素朴に作ると、
    // 偽物で緑になる試験が本物では別の答えを返す（`FindByUsernameAsync` と同じ注記）。
    public Task<IReadOnlyList<IdentityUser>> SearchUsersAsync(
        string query, int max, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || max < 1)
            return Task.FromResult<IReadOnlyList<IdentityUser>>([]);

        return Task.FromResult<IReadOnlyList<IdentityUser>>(
        [
            .. _users.Values
                .Where(u => u.Enabled)
                .Where(u => u.Username.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || u.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(u => u.DisplayName, StringComparer.Ordinal)
                .Take(max)
                .Select(u => u.ToIdentityUser())
        ]);
    }

    // FR-05, FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0088 決定 1, ADR-0098 決定 1,
    // [[IADR-0447]] (#1447): 所属グループ。**鍵は利用者の内部 ID**（本物と同じ意味論）。
    // 🔴 **親へ遡らない**（本物の注記と同じ。木の形が認可の広さを黙って変えないため）。
    public Task<IReadOnlyList<IdentityGroup>> GetUserGroupsAsync(string userId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<IdentityGroup>>(
            Memberships.TryGetValue(userId, out var ids)
                ? [.. Groups.Where(g => ids.Contains(g.Id, StringComparer.Ordinal))]
                : []);

    // FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1, [[IADR-0447]] (#1447): 共有先の候補。
    // 🔴 **本物（Keycloak の平坦化 ＋ 名前の部分一致）と同じ意味論にする** ——
    // 大小文字無視・パス順・`max` 件（`SearchUsersAsync` と同じ注記の理由）。
    public Task<IReadOnlyList<IdentityGroup>> SearchGroupsAsync(
        string query, int max, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || max < 1)
            return Task.FromResult<IReadOnlyList<IdentityGroup>>([]);

        return Task.FromResult<IReadOnlyList<IdentityGroup>>(
        [
            .. Groups
                .Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(g => g.Path, StringComparer.Ordinal)
                .Take(max)
        ]);
    }

    // FR-19, SC-19 主要素 3, 計画 ADR-0098 決定 1, [[IADR-0447]] (#1447): ID → 像。
    // 🔴 **無い ID は落ちる（エラーではない）・要求順を保つ**（本物と同じ意味論）。
    public Task<IReadOnlyList<IdentityGroup>> GetGroupsByIdsAsync(
        IReadOnlyList<string> ids, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<IdentityGroup>>(
        [
            .. ids
                .Select(id => Groups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal)))
                .Where(g => g is not null)
                .Select(g => g!)
        ]);

    public Task<IReadOnlyList<string>> ListAssignableRolesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>([.. AssignableRoles]);

    // 🔴 **予約キー（保持起点）は差し替えで消さない**（[[IADR-0428]] / #1392）。
    // 本物（Keycloak 実装）と**同じ意味論**にしておく —— ここだけ素朴に置き換えると、
    // 偽物で緑になる試験が本物では別の答えを返す。
    public Task<IdentityUser?> ReplaceAttributesAsync(
        string userId, IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
        => Task.FromResult(Mutate(userId, u => u.Attributes =
            new Dictionary<string, string>(
                RetentionAnchorAttributes.PreserveReserved(u.Attributes, attributes),
                StringComparer.Ordinal)));

    // FR-19, SC-17, ADR-0082 決定 5, [[IADR-0428]] (#1392): 保持起点の書き込み・消去。
    public Task<IdentityUser?> SetRetentionAnchorAsync(
        string userId, string attributeKey, DateTimeOffset? anchorAt, CancellationToken ct)
        => Task.FromResult(Mutate(userId, u =>
        {
            if (anchorAt is { } at) u.Attributes[attributeKey] = RetentionAnchorPolicy.Format(at);
            else u.Attributes.Remove(attributeKey);
        }));

    public Task<IdentityUser?> ReplaceRealmRolesAsync(
        string userId, IReadOnlyList<string> roles, CancellationToken ct)
        => Task.FromResult(Mutate(userId, u => u.Roles = [.. roles]));

    public Task<IdentityUser?> SetEnabledAsync(string userId, bool enabled, CancellationToken ct)
        => Task.FromResult(Mutate(userId, u => u.Enabled = enabled));

    public Task<bool> RevokeSessionsAsync(string userId, CancellationToken ct)
    {
        // セッションはこの偽物が持たない（BFF 側の RedisTicketStore が持つ）。**要求できたかだけを返す。**
        if (!_users.ContainsKey(userId)) return Task.FromResult(false);
        _revoked.Enqueue(userId);
        return Task.FromResult(true);
    }

    private IdentityUser? Mutate(string userId, Action<MutableUser> apply)
    {
        if (!_users.TryGetValue(userId, out var user)) return null;
        apply(user);
        return user.ToIdentityUser();
    }

    private sealed class MutableUser
    {
        public required string Id { get; init; }
        public required string Username { get; init; }
        public required string DisplayName { get; init; }
        public bool Enabled { get; set; }
        public List<string> Roles { get; set; } = [];
        public Dictionary<string, string> Attributes { get; set; } = [];

        public IdentityUser ToIdentityUser()
            => new(Id, Username, DisplayName, Enabled, [.. Roles], new Dictionary<string, string>(Attributes));
    }
}
