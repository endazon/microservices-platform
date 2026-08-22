namespace Platform.Bff.Foundation.Session;

// NFR, ADR-0032, IADR-0250, #439: BFF セッション（Token Handler パターン）の構成値。
//
// **ブラウザはセッションキーだけを持ち、アクセストークン／リフレッシュトークンは BFF 側にのみ置く。**
// 値の既定はローカル開発が動く形にしてあるが、**本番は構成で上書きする**（とくに ClientSecret）。
public sealed class BffSessionOptions
{
    public const string SectionName = "BffSession";

    /// <summary>OIDC の発行元。既定はサービス間と同じ in-cluster の Keycloak。</summary>
    public string Authority { get; set; } = "http://keycloak:8080/realms/platform";

    /// <summary>
    /// metadata の取得先を issuer と分離する場合に設定する（IADR-0076 手順B / IADR-0086 と同じ理由）。
    /// 空なら <see cref="Authority"/> を使う。
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>ブラウザが受け取る iss がエッジ host のとき、追加で受理する issuer。</summary>
    public string? ValidIssuers { get; set; }

    /// <summary>OIDC クライアント ID。realm の `bff` クライアント。</summary>
    public string ClientId { get; set; } = "bff";

    /// <summary>
    /// 🔴 **コンフィデンシャルクライアントの secret。リポジトリへ実値を置かない。**
    /// realm には他のクライアントと同じく `*-dev-secret-change-me` の置き場だけがあり、
    /// 実値は k8s Secret から環境変数で注入する（minio / grafana / vault と同じ形）。
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>平文 http のローカル開発を許すか。**本番で true にしない。**</summary>
    public bool RequireHttpsMetadata { get; set; }

    /// <summary>セッション実体の置き場（Redis）。ADR-0032 §決定 が Redis と定めている。</summary>
    public string RedisConnectionString { get; set; } = "redis:6379";

    /// <summary>
    /// セッション Cookie 名。`__Host-` 接頭辞は Secure ＋ Path=/ ＋ Domain 無しを
    /// **ブラウザ側で強制する**（IADR-0250 の Cookie 属性と整合する）。
    /// </summary>
    public string CookieName { get; set; } = "__Host-msp-session";

    /// <summary>
    /// CSRF 対策のカスタムヘッダ名（IADR-0250 決定 1）。
    /// **値は何でもよい。存在することが preflight を強制する**ことに意味がある。
    /// </summary>
    public string CsrfHeaderName { get; set; } = "X-MSP-CSRF";

    /// <summary>
    /// セッションの寿命（秒）。**realm の値に合わせる**（IADR-0250 決定 6）。
    /// フレームワーク既定の 14 日には根拠が無いので使わない。
    /// </summary>
    public int SessionLifetimeSeconds { get; set; } = 2592000;
}
