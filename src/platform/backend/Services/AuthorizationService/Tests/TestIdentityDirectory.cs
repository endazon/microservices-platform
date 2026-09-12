using AuthorizationService.Domain.Ports;
using AuthorizationService.Infrastructure.ExternalServices;
using Microsoft.Extensions.DependencyInjection;

namespace AuthorizationService.Tests;

// FR-05, FR-16, NFR-09, UC-09, SC-12, 計画 ADR-0088 決定 1, [[IADR-0413]] (#1333):
// 🔴 **ABAC 判定に使う属性は IdP から引き直される。要求本文の主張は評価に用いられない。**
// したがってテストは「利用者がどんな属性を持つか」を**ここへ**置く（要求本文へではない）。
//
// 🔴 **器は 2 つある**（TestServer の `TestWebApplicationFactory` と実 Kestrel の `GrpcKestrelFactory`）。
// **差し替えの規則はこの 1 か所が持つ** —— 器ごとに書くと片方だけが古くなる
// （[[IADR-0412]] 決定 6 / [[IADR-0411]] と同じ理由）。
public sealed class TestIdentityDirectory
{
    /// <summary>
    /// 利用者名 → ABAC 属性。**ここに無い名前は本物の偽物（`InMemoryIdentityAdminClient`）へ素通しする。**
    /// 🔴 「未知の名前は実在する」という既定を置かない —— 置くと、名簿の不在を測る試験
    /// （`GrpcUserDirectoryTests`）が**この器のせいで**緑になる。
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> Attributes { get; } = new(StringComparer.Ordinal);

    /// <summary>名簿に**居ない**ことにする利用者名（「居ない」は**応答**で deny）。</summary>
    public HashSet<string> Unknown { get; } = new(StringComparer.Ordinal);

    /// <summary>IdP が**引けない**ことにする（「引けなかった」は **status**。503 / `UNAVAILABLE`）。</summary>
    public Exception? Failure { get; set; }

    /// <summary>`FindByUsernameAsync` が受け取った利用者名（引き直しが実際に走った観測点）。</summary>
    public List<string> LookedUp { get; } = [];

    /// <summary>
    /// FR-19, 計画 ADR-0036 D-03, ADR-0098 決定 1, [[IADR-0447]] (#1447):
    /// 利用者名 → **所属グループ ID**（`${current_groups}` の束縛値）。
    /// **ここに無い名前は本物の偽物（`InMemoryIdentityAdminClient`）へ素通しする**（属性と同じ規則）。
    ///
    /// 🔴 **鍵は利用者名である**（本物のポートは内部 ID を取るが、器へ書くのは名前のほうが
    /// 試験の意図に近い）。この器が作る内部 ID は `id-&lt;利用者名&gt;` なので、受け取った ID から
    /// 接頭辞を落として引く。
    /// </summary>
    public Dictionary<string, string[]> Groups { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// #1447: **所属照会だけが引けない**ことにする（属性は引ける）。
    /// 🔴 <see cref="Failure"/> と別に持つ —— 同じものにすると「属性は引けたが所属が引けなかった」
    /// ときに 503 へ倒れることを測れない（`ADR-0088` 決定 1 を所属にも通す試験）。
    /// </summary>
    public Exception? GroupFailure { get; set; }

    /// <summary>`GetUserGroupsAsync` が受け取った ID（所属照会が実際に走った観測点）。</summary>
    public List<string> GroupsLookedUp { get; } = [];

    /// <summary>この器が作る内部 ID の接頭辞（`id-&lt;利用者名&gt;`）。</summary>
    internal const string StubIdPrefix = "id-";

    /// <summary>
    /// 包んだ本物の偽物。SC-17 の試験（失効要求の観測など）はこちらを見る。
    /// 🔴 **装飾を挟んだので `GetRequiredService<IIdentityAdminClient>()` のキャストでは取れない。**
    /// </summary>
    public IIdentityAdminClient Inner { get; private set; } = null!;

    public void Reset()
    {
        Attributes.Clear();
        Unknown.Clear();
        Failure = null;
        LookedUp.Clear();
        // #1447: 所属の状態も戻す（片方だけ戻すと、次のクラスが前のクラスの所属を見る）。
        Groups.Clear();
        GroupFailure = null;
        GroupsLookedUp.Clear();
    }

    /// <summary>
    /// 既存の `IIdentityAdminClient` 登録を**包む**（取り除いてから包む）。
    /// 🔴 消さずに足すと「最後の登録が解決される」形に依存し、登録順を変えた誰かが黙って本物へ戻す。
    /// </summary>
    public static void Replace(IServiceCollection services, TestIdentityDirectory state)
    {
        var registered = services.Single(d => d.ServiceType == typeof(IIdentityAdminClient));
        services.Remove(registered);
        services.AddSingleton<IIdentityAdminClient>(sp =>
        {
            state.Inner = (IIdentityAdminClient)ActivatorUtilities.CreateInstance(
                sp, registered.ImplementationType ?? typeof(InMemoryIdentityAdminClient));
            return new StubIdentityAdminClient(state.Inner, state);
        });
    }

    // 🔴 **`FindByUsernameAsync` だけを操作し、他の口は素通しする** ——
    // SC-17 の利用者管理テストは本物の `InMemoryIdentityAdminClient` を見ており、
    // そこへ別物を挿すと「何を測っているのか」が変わる。
    private sealed class StubIdentityAdminClient(IIdentityAdminClient inner, TestIdentityDirectory state)
        : IIdentityAdminClient
    {
        public Task<IdentityUser?> FindByUsernameAsync(string username, CancellationToken ct)
        {
            lock (state.LookedUp) state.LookedUp.Add(username);

            if (state.Failure is not null) throw state.Failure;
            if (state.Unknown.Contains(username)) return Task.FromResult<IdentityUser?>(null);

            // 器が意見を持たない名前は**本物の偽物へ素通しする**（上の 🔴）。
            if (!state.Attributes.TryGetValue(username, out var attributes))
                return inner.FindByUsernameAsync(username, ct);

            return Task.FromResult<IdentityUser?>(
                new IdentityUser($"{StubIdPrefix}{username}", username, username, true, [], attributes));
        }

        // FR-19, 計画 ADR-0036 D-03, ADR-0088 決定 1, ADR-0098 決定 1, [[IADR-0447]] (#1447):
        // 所属照会。**属性と同じ規則**（器が意見を持たない名前は本物の偽物へ素通しする）。
        // 🔴 グループの像は ID だけが判定に効く（名前・パスは画面の表示用）ので、
        // 器は ID から機械的に組み立てる。
        public Task<IReadOnlyList<IdentityGroup>> GetUserGroupsAsync(string userId, CancellationToken ct)
        {
            lock (state.GroupsLookedUp) state.GroupsLookedUp.Add(userId);

            if (state.GroupFailure is not null) throw state.GroupFailure;

            var username = userId.StartsWith(StubIdPrefix, StringComparison.Ordinal)
                ? userId[StubIdPrefix.Length..]
                : userId;
            if (!state.Groups.TryGetValue(username, out var ids))
                return inner.GetUserGroupsAsync(userId, ct);

            return Task.FromResult<IReadOnlyList<IdentityGroup>>(
                [.. ids.Select(id => new IdentityGroup(id, id, "/" + id))]);
        }

        // FR-19, SC-19 主要素 3, [[IADR-0447]] (#1447): グループ検索・ID 引き当ては**素通しする**
        // （SC-19 の試験は本物の偽物が持つグループ木を見る。操作されるのは所属照会だけである）。
        public Task<IReadOnlyList<IdentityGroup>> SearchGroupsAsync(string query, int max, CancellationToken ct)
            => inner.SearchGroupsAsync(query, max, ct);

        public Task<IReadOnlyList<IdentityGroup>> GetGroupsByIdsAsync(
            IReadOnlyList<string> ids, CancellationToken ct)
            => inner.GetGroupsByIdsAsync(ids, ct);

        public Task<IReadOnlyList<IdentityUser>> ListUsersAsync(CancellationToken ct) => inner.ListUsersAsync(ct);
        // FR-19, SC-19 主要素 3, [[IADR-0445]] (#1445): 共有先の候補の検索も**素通しする**
        // （SC-19 の試験は本物の偽物が持つ名簿を見る。`FindByUsernameAsync` だけが操作される）。
        public Task<IReadOnlyList<IdentityUser>> SearchUsersAsync(string query, int max, CancellationToken ct)
            => inner.SearchUsersAsync(query, max, ct);
        public Task<IReadOnlyList<string>> ListAssignableRolesAsync(CancellationToken ct) => inner.ListAssignableRolesAsync(ct);
        public Task<IdentityUser?> ReplaceAttributesAsync(
            string userId, IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
            => inner.ReplaceAttributesAsync(userId, attributes, ct);
        // FR-19, SC-17, ADR-0082 決定 5, [[IADR-0428]] (#1392): 保持起点の書き込みも素通しする
        // （SC-17 の試験は本物の偽物が持つ属性を見る）。
        public Task<IdentityUser?> SetRetentionAnchorAsync(
            string userId, string attributeKey, DateTimeOffset? anchorAt, CancellationToken ct)
            => inner.SetRetentionAnchorAsync(userId, attributeKey, anchorAt, ct);
        public Task<IdentityUser?> ReplaceRealmRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken ct)
            => inner.ReplaceRealmRolesAsync(userId, roles, ct);
        public Task<IdentityUser?> SetEnabledAsync(string userId, bool enabled, CancellationToken ct)
            => inner.SetEnabledAsync(userId, enabled, ct);
        public Task<bool> RevokeSessionsAsync(string userId, CancellationToken ct) => inner.RevokeSessionsAsync(userId, ct);
    }
}
