namespace McpServer.Api.Foundation.Domain;

// FR-16, UC-08, ADR-0024 §3: MCP を呼び出す主体の種別。
// 有人（利用者の代理・Authorization Code + PKCE）と無人（サービスアカウント・Client Credentials）。
public enum McpClientKind
{
    // 有人エージェント（利用者の代理）。
    Interactive = 0,

    // 無人エージェント（サービスアカウント）。ADR-0034 決定 9 の個人資料一律除外の対象。
    ServiceAccount = 1
}

// FR-16, UC-09, SC-12: 登録済み MCP クライアント。
// Keycloak のクライアント登録に対応するプラットフォーム側の有効・無効と ABAC 属性割当を持つ。
public class McpClient
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    // Keycloak のクライアント ID（トークンの `azp` / `client_id`）。
    public string ClientId { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public McpClientKind Kind { get; private set; }

    // UC-09 例外フロー・SC-12: 無効化したクライアントは接続・実行を拒否する。
    public bool Enabled { get; private set; } = true;

    // ADR-0024 §3: 無人アカウントへ明示的に割り当てる ABAC 属性。
    public Dictionary<string, string> Attributes { get; private set; } = [];

    // ADR-0024 §4: このクライアント側 LLM のデータ保護水準ティア（08_data-egress-policy）。
    // **既定は最も低い保護水準（ティアC）**とする —— 未申告のクライアントへ本文を出さない側へ倒す
    // （同 §基本原則「既定は安全側」）。値の型は Services/EgressPolicy.cs の EgressTier。
    public int EgressTier { get; private set; } = (int)Services.EgressTier.StandardExternal;

    public DateTimeOffset RegisteredAt { get; private set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private McpClient() { }

    public static McpClient Register(
        string clientId, string displayName, McpClientKind kind,
        IReadOnlyDictionary<string, string>? attributes, Services.EgressTier egressTier,
        DateTimeOffset now)
        => new()
        {
            ClientId = clientId,
            DisplayName = displayName,
            Kind = kind,
            Enabled = true,
            EgressTier = (int)egressTier,
            Attributes = attributes is null ? [] : new Dictionary<string, string>(attributes),
            RegisteredAt = now,
            UpdatedAt = now
        };

    // UC-09: 無効化・再有効化。次の呼び出しから即座に効く（キャッシュを挟まない）。
    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        Enabled = enabled;
        UpdatedAt = now;
    }

    public void ReplaceAttributes(IReadOnlyDictionary<string, string> attributes, DateTimeOffset now)
    {
        Attributes = new Dictionary<string, string>(attributes);
        UpdatedAt = now;
    }
}

// FR-16, UC-08: 解決済みの実行主体。トークンから組み立てる。
//
// 🔴 IsServiceAccount が **ADR-0034 決定 9 の適用条件そのもの**である。ここを取り違えると
// 個人資料の一律除外が静かに外れるため、判定は 1 箇所（McpSubjectResolver）に閉じる。
public sealed record McpSubject(
    string SubjectId,
    string ClientId,
    McpClientKind Kind,
    IReadOnlyDictionary<string, string> Attributes)
{
    public bool IsServiceAccount => Kind == McpClientKind.ServiceAccount;
}
