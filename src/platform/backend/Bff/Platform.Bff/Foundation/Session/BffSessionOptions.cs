using StackExchange.Redis;

namespace Platform.Bff.Foundation.Session;

// NFR, ADR-0032, IADR-0251, #439: BFF セッション（Token Handler パターン）の構成値。
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
    /// 実値は k8s Secret から環境変数で注入する（grafana / vault と同じ形）。
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>平文 http のローカル開発を許すか。**本番で true にしない。**</summary>
    public bool RequireHttpsMetadata { get; set; }

    /// <summary>
    /// セッション実体・鍵リング・SC-22 の書き込み記録の置き場（RESP のストア）の接続先。パスワードは含めない。
    /// ADR-0032 §決定 の「Redis」は計画 ADR-0131 決定 2 により「Redis 互換（Valkey）」と読む。
    /// 既定 `valkey:6379` は compose のサービス名と経路 B の ExternalName に一致するので、配備で上書きしない
    /// （IADR-0316 の判断を IADR-0522 が引き継ぐ）。
    /// </summary>
    public string RedisConnectionString { get; set; } = "valkey:6379";

    /// <summary>
    /// 🔴 **セッションストアの認証パスワード。リポジトリへ実値を置かない。**
    /// ストアは認証を必須にして起動する（パスワードが空なら起動しない。計画 ADR-0131 決定 4 の 2・IADR-0522）。
    /// 実値は k8s Secret から環境変数で注入する（`session-store-credentials` の `password`。compose は `SESSION_STORE_PASSWORD`）。
    /// </summary>
    public string RedisPassword { get; set; } = string.Empty;

    /// <summary>
    /// StackExchange.Redis へ渡す構成文字列（接続先 ＋ パスワード）。セッション・鍵リング・ヘルスチェックが
    /// **同じ 1 つ**を使う（置き場が 2 つあると、片方だけ認証が通る状態を作れてしまう。IADR-0522）。
    /// </summary>
    public string SessionStoreConfiguration()
    {
        var configuration = ConfigurationOptions.Parse(RedisConnectionString);
        if (!string.IsNullOrEmpty(RedisPassword))
            configuration.Password = RedisPassword;
        return configuration.ToString(includePassword: true);
    }

    /// <summary>構成から <see cref="BffSessionOptions"/> を読む（ヘルスチェックと <c>AddBffSession</c> が同じ読み方をする）。</summary>
    public static BffSessionOptions From(IConfiguration config)
    {
        var options = new BffSessionOptions();
        config.GetSection(SectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// セッション Cookie 名。`__Host-` 接頭辞は Secure ＋ Path=/ ＋ Domain 無しを
    /// **ブラウザ側で強制する**（IADR-0251 の Cookie 属性と整合する）。
    /// </summary>
    public string CookieName { get; set; } = "__Host-msp-session";

    /// <summary>
    /// CSRF 対策のカスタムヘッダ名（IADR-0251 決定 1）。
    /// **値は何でもよい。存在することが preflight を強制する**ことに意味がある。
    /// </summary>
    public string CsrfHeaderName { get; set; } = "X-MSP-CSRF";

    /// <summary>
    /// セッションの寿命（秒）。**realm の値に合わせる**（IADR-0251 決定 6）。
    /// フレームワーク既定の 14 日には根拠が無いので使わない。
    /// </summary>
    public int SessionLifetimeSeconds { get; set; } = 2592000;
}
