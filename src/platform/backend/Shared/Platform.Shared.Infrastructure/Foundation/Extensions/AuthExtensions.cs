using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace Platform.Shared.Infrastructure.Foundation.Extensions;

// ADR-0004, FR-09: 認可ポリシー／ロールの名称定数（各サービスから参照）
public static class PlatformAuthPolicies
{
    // FR-09: 属性辞書・ABAC ポリシーの管理は管理者のみ許可する。
    public const string AdminOnly = "AdminOnly";

    // FR-15, SC-11, IADR-0030: 構成情報の閲覧は管理者・運用者ロールに限定する。
    public const string ConfigViewer = "ConfigViewer";

    // 管理者ロール（Keycloak のレルムロール想定）。
    public const string AdminRole = "platform-admin";

    // FR-15, SC-11, IADR-0030: 運用者ロール（Keycloak のレルムロール想定）。
    // ［2026-09-14 / #1411］従前の注記「構成閲覧のみ。管理系操作は不可」は実態より狭かった
    // （辺の型辞書の管理は既に admin ＋ operator）。**計画 SC-22 は秘密情報の投入を運用者にも開く**
    // （ADR-0042 決定 2・IADR-0453 決定 1）。運用者に何を許すかは各ポリシーが持つ。
    public const string OperatorRole = "platform-operator";

    // SC-22, FR-05, ADR-0095 決定 3, ADR-0042 決定 2, IADR-0453 決定 1 (#1411): 秘密情報の投入は
    // 運用者・システム管理者に限る。🔴 **`ConfigViewer` を流用しない** —— 集合は同じだが、
    // 閲覧の名前に書き込みを相乗りさせると、構成閲覧の公開範囲を変えたときに秘密の書き込みまで黙って動く。
    public const string SecretItemWriter = "SecretItemWriter";

    // NFR-09, ADR-0029, ADR-0075, IADR-0379 決定 4 (#1201): east-west gRPC の呼び出し側サービスに要求する
    // ポリシー。**利用者のロール（AdminOnly / ConfigViewer）とは別軸**であり、利用者のトークンでは通らない
    // （通ると「利用者が直接呼んだ」と区別できず、confused deputy になる）。
    public const string ServiceCaller = "ServiceCaller";

    // FR-19, SC-19 主要素 3, 計画 ADR-0098 決定 1, **ADR-0100 決定 1・フォローアップ 2**,
    // [[IADR-0401]] 決定 2, [[IADR-0449]] (#1447): **人の主体だけを通す**（認証済み かつ
    // 無人の主体ではない）。名簿・グループ名簿の読み口に課す。
    //
    // 🔴 **ロールの軸ではない。主体の種別の軸である。** 計画 `ADR-0100` 決定 1 は利用者検索の
    // 到達範囲を「全利用者」と定め（ロールで絞らない）、フォローアップ 2 は
    // **realm のサービスアカウント（`platform-service`）も認証済みなので到達できてしまう**ことを
    // 実装側へ戻した。したがって絞る軸は**ロールではなく主体の種別**である ——
    // `AdminOnly` を持つサービスアカウント（`abac-seeder`）も通さない。
    //
    // 🔴 **`ServiceCaller` の裏返しではない。** あちらは「`platform-service` を持つこと」であり、
    // ロールを持たない機械クライアントを通してしまう。こちらは
    // `MachinePrincipal.IsMachine`（トークンが名乗る形だけで判定・許可集合を構成に持たない）の否定である。
    //
    // 🔴 **[[IADR-0401]] 決定 2 の分界を名簿の側で保つ。** 名簿の列挙は s2s の面へ出さない ——
    // サービス間で利用者を引く経路は gRPC `UserDirectory` の狭い読み口だけであり、
    // 一般利用者向けの検索（`lookup` / `resolve`）は**人の操作**である。
    public const string InteractiveUser = "InteractiveUser";

    // サービスアカウント（client credentials で得た JWT）に付けるレルムロール。realm の各 confidential client の
    // service account へ付与する（deploy/keycloak/microservices-platform-realm.json）。
    public const string ServiceRole = "platform-service";
}

public static class AuthExtensions
{
    // NFR-09, 計画 ADR-0036, ADR-0086, [[IADR-0523]] (#1846 / planning#770。利用者裁定 2026-10-09 の方式 C):
    // platform の API が受け付ける**共有の audience**。realm のクライアントスコープ `platform-api-audience`
    // （`oidc-audience-mapper`）が、正当な呼び出し元のクライアントのトークンにだけ載せる
    // （deploy/keycloak/microservices-platform-realm.json）。
    public const string DefaultAudience = "platform-api";

    // 受け付ける audience の構成キー。未設定なら <see cref="DefaultAudience"/> ただ 1 つ。
    // サービスごとの audience へ移るときは、この構成と realm の写像だけを変える（コードは変えない）。
    public const string AudiencesConfigKey = "Auth:Audiences";

    // 🔴 FR-16, 計画 ADR-0134 決定 1, #1854: `mcp-server` は「MCP クライアントに発行したトークン」の意味を持ち、
    // McpServer の `/mcp`（`McpAudience` スキーム）だけが受け付ける。既定のスキームに入れると MCP クライアントの
    // トークンが platform の API を通ってしまうため、構成で与えられても起動を止める。
    public const string ReservedMcpAudience = "mcp-server";

    // ADR-0004: Keycloak OIDC/JWT 認証（P0: 認証のみ、P2: ABAC 認可を追加）
    public static IServiceCollection AddPlatformAuth(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(PlatformJwtBearer(config));

        // FR-09, ADR-0004: Keycloak の realm_access.roles を ClaimTypes.Role へ展開する。
        // これがないと RequireRole("platform-admin") が実トークンにマッチしない。
        services.AddTransient<IClaimsTransformation, KeycloakRolesClaimsTransformation>();

        // FR-09: 管理系エンドポイント用に管理者ロールポリシーを登録する。
        services.AddAuthorization(options =>
        {
            options.AddPolicy(PlatformAuthPolicies.AdminOnly, policy =>
                policy.RequireRole(PlatformAuthPolicies.AdminRole));

            // FR-15, SC-11, IADR-0030: 構成情報 API・構成ビューア用。
            // RequireRole の複数指定はいずれか一致（OR）で許可する。
            options.AddPolicy(PlatformAuthPolicies.ConfigViewer, policy =>
                policy.RequireRole(
                    PlatformAuthPolicies.AdminRole,
                    PlatformAuthPolicies.OperatorRole));

            // SC-22, ADR-0095, IADR-0453 決定 1 (#1411): 秘密情報の投入（BFF の /bff/secrets）。
            options.AddPolicy(PlatformAuthPolicies.SecretItemWriter, policy =>
                policy.RequireRole(
                    PlatformAuthPolicies.AdminRole,
                    PlatformAuthPolicies.OperatorRole));

            // NFR-09, IADR-0379 決定 4 (#1201): east-west gRPC の面に掛ける。呼び出し側サービス自身の
            // 資格情報（`platform-service`）だけを通し、利用者のトークンは（管理者であっても）通さない。
            options.AddPolicy(PlatformAuthPolicies.ServiceCaller, policy =>
                policy.RequireRole(PlatformAuthPolicies.ServiceRole));

            // FR-19, SC-19 主要素 3, 計画 ADR-0098 決定 1, ADR-0100 決定 1・フォローアップ 2,
            // [[IADR-0401]] 決定 2, [[IADR-0449]] (#1447): 名簿の読み口は**人の主体だけ**。
            // 🔴 **ロールを一切要求しない**（共有は一般利用者の操作である）。絞るのは主体の種別で、
            // 判定は `MachinePrincipal.IsMachine` ただ 1 つに委ねる（サービスアカウントの一覧を
            // 構成に持たない ＝ 統制の抜け道を作らない。[[IADR-0420]]）。
            options.AddPolicy(PlatformAuthPolicies.InteractiveUser, policy =>
                policy.RequireAuthenticatedUser()
                    .RequireAssertion(ctx => !MachinePrincipal.IsMachine(ctx.User)));
        });
        return services;
    }

    // FR-16, 計画 ADR-0134 決定 1・フォローアップ 2, #1844: 既定の JWT スキームの設定（発行元・メタデータ・名前とロールのクレーム）。
    // **既定のスキームと、audience を検証する別のスキーム（McpServer の `/mcp`）が同じ 1 つを使う**ために切り出した
    // （発行元の検証を 2 つにしない）。
    // ［2026-10-09 / #1846 / [[IADR-0523]]］audience を検証する（`ValidateAudience = true`・`ValidAudiences = Auth:Audiences`）。
    //   `/mcp` のスキームは呼んだ後で audience を `mcp-server` だけへ置き換える（`McpAudienceAuthentication`）。
    // 🔴 構成は**呼んだ時点で読む**（切り出す前と同じく登録の時点の値。返す関数の中で読み直さない）。
    //   audience の構成が空・`mcp-server` を含むときは**ここで例外を投げる**（＝サービスの起動の時点で止まる。fail-fast）。
    public static Action<JwtBearerOptions> PlatformJwtBearer(IConfiguration config)
    {
        var authority = config["Auth:Authority"]
            ?? "http://keycloak:8080/realms/platform";

        // NFR(運用性/セキュリティ), ADR-0004, IADR-0076 手順B, IADR-0086: OIDC metadata の取得先と issuer 検証値を
        // 分離できるようにする（単一エッジ host OIDC を CoreDNS/hosts 改変なしに成立させる）。既定（両キー未設定）は
        // Authority 一本の現行挙動に縮退＝後方互換・fail-safe。issuer 検証は弱めない（下記参照）。
        var metadataAddress = config["Auth:MetadataAddress"];
        var validIssuers = ParseValidIssuers(config["Auth:ValidIssuers"]);
        var validAudiences = ResolveAudiences(config);

        return options =>
        {
            // IADR-0086 決定1: MetadataAddress 設定時は in-cluster から到達できる well-known を metadata 取得先に
            // 用い、Authority とは排他にする（両設定時の暗黙優先順位に依存しない）。未設定時は現行どおり Authority。
            if (!string.IsNullOrWhiteSpace(metadataAddress))
            {
                options.MetadataAddress = metadataAddress;
            }
            else
            {
                options.Authority = authority;
            }
            options.RequireHttpsMetadata = false;
            // NFR-09, 計画 ADR-0036, [[IADR-0523]] (#1846): audience を検証する。`aud` が無いトークン・
            // 運用ツールや MCP クライアントに発行されたトークン（`aud` に platform-api が無い）は 401 になる。
            options.TokenValidationParameters.ValidateAudience = true;
            options.TokenValidationParameters.ValidAudiences = validAudiences;
            // IADR-0086 決定3: issuer 検証は弱めない（ValidateIssuer=true のまま）。ValidIssuers は「token の iss として
            // 追加で受理する発行元 URL の許可リスト」で、エッジ host issuer（手順B）を足す。JwtBearer ハンドラは
            // metadata 由来の issuer を常に受理集合へ併合するため、in-cluster issuer（手順A）と併存＝後方互換。
            // 署名鍵(JWKS)は信頼できる in-cluster metadata から取得するため、許可リスト追加で攻撃面は広がらない。
            if (validIssuers.Length > 0)
            {
                options.TokenValidationParameters.ValidIssuers = validIssuers;
            }
            // FR-09: RequireRole/IsInRole が参照するロールクレーム型を明示する。
            // 実 Keycloak のレルムロールは realm_access.roles に格納され、標準ハンドラでは
            // ClaimTypes.Role へ展開されないため、下記の IClaimsTransformation で補う。
            options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
            // FR-08, FR-15, IADR-0010, IADR-0031: Identity.Name が参照する名前クレームを Keycloak の
            // preferred_username に合わせる。既定マップは unique_name のみ写像するため、実
            // Keycloak トークンでは Name が null になり、送信者特定（FR-08 の userId・構成 API
            // 監査ログの subject）が全員 anonymous/unknown へ潰れる（Issue #118 監査で実測）。
            options.TokenValidationParameters.NameClaimType = "preferred_username";
        };
    }

    // NFR-09, [[IADR-0523]] (#1846): 受け付ける audience を構成から決める。
    //   - 未設定 → [platform-api]。
    //   - 配列（`Auth:Audiences:0` …）か単一の文字列（`Auth__Audiences=a,b`。カンマ/空白区切り）を受ける。
    //   - 🔴 設定されているのに空（`Auth__Audiences=""`）→ 例外（「検証しない」へ黙って縮退させない）。
    //   - 🔴 `mcp-server` を含む → 例外（上の <see cref="ReservedMcpAudience"/>）。
    internal static string[] ResolveAudiences(IConfiguration config)
    {
        var section = config.GetSection(AudiencesConfigKey);
        if (!section.Exists())
        {
            return [DefaultAudience];
        }
        var children = section.GetChildren().ToArray();
        var values = (children.Length > 0
                ? children.Select(c => c.Value).SelectMany(v => SplitList(v))
                : SplitList(section.Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (values.Length == 0)
        {
            throw new InvalidOperationException(
                $"{AudiencesConfigKey} が空です。受け付ける audience を 1 つ以上設定するか、キーを外して既定（{DefaultAudience}）を使ってください（#1846）。");
        }
        if (values.Contains(ReservedMcpAudience, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"{AudiencesConfigKey} に {ReservedMcpAudience} は使えません。MCP クライアントのトークンの audience であり、McpServer の /mcp だけが受け付けます（計画 ADR-0134 決定 1・#1846）。");
        }
        return values;
    }

    private static string[] SplitList(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // IADR-0086 決定1: Auth:ValidIssuers を単一 env 文字列（chart から注入）としてカンマ/空白区切りでパースする。
    // 空要素は落とし、各要素を trim する。未設定/空なら空配列（＝ValidIssuers を設定しない＝現行挙動）。
    private static string[] ParseValidIssuers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        return raw
            .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }
}
