namespace Platform.Shared.Contracts.Dtos;

// FR-16, FR-09, SC-12, SC-17, ADR-0062 決定 2, IADR-0385 (#1243):
// **集合値の利用者属性**（`tags` / `projects`）を 1 キー 1 値の契約へ載せるときの符号化。
//
// ■ 🔴 なぜ契約プロジェクトに置くのか
//   従前、符号化は 3 者で食い違っていた —— Keycloak は多値配列を**先頭 1 値へ畳み**
//   （`KeycloakIdentityAdminClient.ToIdentityUser`）、SC-17 の辞書照合は値**全体**を突き合わせ
//   （`UserAssignmentValidation`）、MCP は**カンマ／空白区切り**で分割する
//   （`ServiceAccountAttributeSubset.Tokens`）。**同じ文字列が読み手ごとに違う意味を持っていた。**
//   AuthorizationService と McpServer は互いを直接参照できない（`src/README.md` の依存規則）ため、
//   規則を各側へ写すと**その食い違いをそのまま再生産する**。**契約の側に 1 つだけ持つ。**
//
// ■ 対象は 2 キーだけである
//   `07_abac-attribute-model` §利用者属性の `projects`（参加プロジェクト）と、
//   `ADR-0062` 決定 2 が「タグの**集合**」と書く `tags`。
//   🔴 **`clearance` / `department` は単一値であり、ここへ足してはならない** ——
//   一律にカンマ連結すると `clearance: ["internal","public"]` が `"internal,public"` になり、
//   階段ポリシーがどれもマッチしなくなる（deny 側だが**静かに壊れる**）。
public static class UserAttributeEncoding
{
    /// <summary>集合値キーを 1 キー 1 値の契約へ載せるときの区切り。</summary>
    public const string Separator = ",";

    /// <summary>主体側の集合値キー。**綴りは属性辞書（SC-09）と計画の表に合わせる。**</summary>
    public const string TagsKey = "tags";

    /// <summary>主体側の参加プロジェクト（`07_abac-attribute-model` §利用者属性）。</summary>
    public const string ProjectsKey = "projects";

    /// <summary>集合として読むキー。**ここに無いキーは 1 値である。**</summary>
    public static IReadOnlySet<string> SetValuedKeys { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TagsKey, ProjectsKey };

    public static bool IsSetValued(string key) => SetValuedKeys.Contains(key);

    /// <summary>
    /// 多値（Keycloak の配列）を契約の線上表現へ連結する。**空白のみの要素は落とす。**
    /// 順序は入力のまま保つ（集合であって列ではないが、表示のために安定させる）。
    /// </summary>
    public static string Join(IEnumerable<string?> values)
        => string.Join(Separator, values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()));

    /// <summary>
    /// 線上表現を集合へ分割する。**分割規則はここ 1 か所である。**
    ///
    /// 🔴 **順序は意味を持たない**（集合であって列ではない）。`"hr,sales"` と `"sales,hr"` は同値。
    /// カンマに加えて空白・タブでも切るのは、人手で入力された値の揺れを吸収するためである
    /// （`ServiceAccountAttributeSubset.Tokens` が従前から採っていた規則を据え置く）。
    /// </summary>
    public static IReadOnlySet<string> Split(string? value)
        => new HashSet<string>(SplitOrdered(value), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <see cref="Split"/> と同じ規則で分割し、**入力の並びのまま**返す。
    /// 集合として比べるなら <see cref="Split"/> を使う —— こちらは Keycloak へ書き戻す配列のように
    /// **並びが観測される**場所のためにある。
    /// </summary>
    public static IReadOnlyList<string> SplitOrdered(string? value)
        => [.. (value ?? string.Empty)
            .Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
