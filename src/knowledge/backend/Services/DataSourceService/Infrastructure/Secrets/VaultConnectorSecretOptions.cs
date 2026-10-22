namespace DataSourceService.Infrastructure.Secrets;

// FR-01, NFR-18, [[IADR-0495]] 決定 3 (#458 段 S1): コネクタの資格情報を読む Vault への接続構成（`Vault:*`）。
//
// 構成キーは BFF の `VaultOptions` と同じ `Vault__Address` / `Vault__Role` / `Vault__AuthMount` であり、
// helm の deployment テンプレートが `services.<name>.vault` から描画する（サービスごとに別の Pod なので衝突しない）。
//
// 🔴 **ここに秘密は無い。** 名乗りは Pod の ServiceAccount トークン（k8s auth）であり、静的な Vault トークンを
// 構成で受け取らない（受け取る口を作ると、それを env へ平文で書く配備が生まれる）。
// したがって `check-secret-injected-options.js` の母集合（「Secret から注入する」と宣言した構成値）には入らない。
public sealed class VaultConnectorSecretOptions
{
    public const string SectionName = "Vault";

    /// <summary>Vault 専用の projected token（audience: vault）の既定位置。helm の deployment テンプレートと同じパス。</summary>
    public const string DefaultServiceAccountTokenPath = "/var/run/secrets/vault/token";

    /// <summary>
    /// Vault の URL（例 `http://vault.platform-infra.svc.cluster.local:8200`）。
    /// **空なら Vault を配備していない構成**として扱い、移送期間用の素通しの解決器だけを配線する
    /// （`vault:` 参照は `ResolverUnavailable` で fail-closed。[[IADR-0493]] 決定 2 と同じ挙動）。
    /// </summary>
    public string? Address { get; set; }

    /// <summary>k8s auth のロール名。policy は `deploy/local/vault/eso/policy-datasource-connector-read.hcl`。</summary>
    public string Role { get; set; } = "datasource-connector-reader";

    /// <summary>k8s auth のマウント名。</summary>
    public string AuthMount { get; set; } = "kubernetes";

    /// <summary>KV v2 のマウント名。</summary>
    public string KvMount { get; set; } = "secret";

    /// <summary>
    /// k8s auth に渡す ServiceAccount トークンの置き場。
    /// NFR-18, IADR-0585（Refs GHSA-j4qv-2w99-xg5p）: 対象者（audience）を秘匿管理（`vault`）に限った projected token の位置
    /// （helm の deployment テンプレートが同じパスへ置く）。k8s API 用の既定のトークンは使わない —— role が audience を要求する。
    /// </summary>
    public string ServiceAccountTokenPath { get; set; } = DefaultServiceAccountTokenPath;

    /// <summary>1 回の HTTP 呼び出しの上限秒数。</summary>
    public int TimeoutSeconds { get; set; } = 10;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Address);

    /// <summary>起動時の検証: 空（Vault なし）か、絶対の http / https の URI だけを許す。</summary>
    public static bool IsAcceptableAddress(string? address) =>
        string.IsNullOrWhiteSpace(address)
        || (Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
}
