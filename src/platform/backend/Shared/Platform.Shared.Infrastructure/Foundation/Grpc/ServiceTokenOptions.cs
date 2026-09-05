namespace Platform.Shared.Infrastructure.Foundation.Grpc;

// NFR-09, ADR-0032, IADR-0379 決定 4 (#1201): サービス間（east-west）呼び出しで呼び出し側が名乗るための
// 資格情報。platform realm の confidential client として OAuth2 client credentials で JWT を得る。
//
// 🔴 **利用者のトークンではない。** BFF セッション（ADR-0032）が持つ利用者のアクセストークンは
// north-south の資格情報であり、サービス間の面へは載せない（載せると呼び出し先が「利用者が直接呼んだ」と
// 区別できず、利用者ロールがサービス間の面へ漏れる）。
//
// ClientSecret の実値は構成から受け取る（appsettings へ書かない）。BFF は自分の confidential client
// （BffSession の ClientId / ClientSecret と同じ client）をそのまま使える —— realm 側で
// serviceAccountsEnabled と `platform-service` ロールを付けてある。
public sealed class ServiceTokenOptions
{
    public const string SectionName = "ServiceToken";

    // トークン端点。未設定なら `Auth:Authority` から `<Authority>/protocol/openid-connect/token` を組む。
    public string? TokenEndpoint { get; set; }

    public string ClientId { get; set; } = "";

    /// <summary>
    /// 呼び出し側サービスの confidential client のシークレット。
    /// 実値は <b>k8s Secret から環境変数で注入する</b>（BffSession__ClientSecret と同型）。
    /// <para>
    /// 🔴 この宣言は <c>scripts/check-secret-injected-options.js</c> の母集合の入口である（IADR-0316 / #1107）。
    /// 宣言があると <c>ServiceToken__ClientSecret</c> が helm に secretKeyRef 由来の env として、
    /// compose に変数展開の env として存在することが機械で要求される。
    /// 既定は空文字なので、注入が抜けると <c>ClientCredentialsServiceTokenProvider</c> が
    /// 「ClientId / ClientSecret が未設定」で例外を投げ、east-west gRPC の呼び出しがすべて落ちる ——
    /// <b>単体テストは構成を自分で与えて走るのでこの欠落では絶対に落ちない</b>（#1107 と同型。IADR-0397）。
    /// </para>
    /// </summary>
    public string ClientSecret { get; set; } = "";

    // 省略可。Keycloak は scope 無しでも client credentials を受け付ける。
    public string? Scope { get; set; }

    // 期限のこれだけ手前で取り直す（時計のずれと往復時間の吸収）。
    public int RefreshSkewSeconds { get; set; } = 30;
}
