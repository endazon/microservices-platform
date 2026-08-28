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

    public Task<IReadOnlyList<string>> ListAssignableRolesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>([.. AssignableRoles]);

    public Task<IdentityUser?> ReplaceAttributesAsync(
        string userId, IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
        => Task.FromResult(Mutate(userId, u => u.Attributes = new Dictionary<string, string>(attributes)));

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
