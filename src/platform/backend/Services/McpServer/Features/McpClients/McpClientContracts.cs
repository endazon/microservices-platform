namespace McpServer.Features.McpClients;

// FR-16, UC-09, SC-12: MCP クライアント登録管理 API の DTO。
// パスは `/mcp-clients`（kebab-case 複数形。ADR-0054 決定 4 が既存 API 命名として挙げた形）。

// 登録要求。Kind は "interactive"（有人）/ "service-account"（無人）。
// EgressTier は "self-hosted" / "protected-external" / "standard-external"（既定は最も低い保護水準）。
// ［2026-10-09 / #1844］RedirectUris は**有人のときだけ必須**（1〜10 件。https か port を明示したループバックの http://127.0.0.1:<port> / http://[::1]:<port>。
// 完全一致で照合する。計画 ADR-0134 決定 1・SC-12 の入力表）。無人には渡さない（渡せば 400）。規則は `Domain/RedirectUriRules`。
public sealed record RegisterMcpClientRequest(
    string ClientId,
    string DisplayName,
    string Kind,
    Dictionary<string, string>? Attributes = null,
    string? EgressTier = null,
    List<string>? RedirectUris = null);

// 属性割当の差し替え要求（無人アカウントの ABAC 属性）。
public sealed record ReplaceMcpClientAttributesRequest(Dictionary<string, string> Attributes);

// 一覧・個別の応答。
public sealed record McpClientView(
    Guid Id,
    string ClientId,
    string DisplayName,
    string Kind,
    bool Enabled,
    IReadOnlyDictionary<string, string> Attributes,
    string EgressTier,
    DateTimeOffset RegisteredAt,
    DateTimeOffset UpdatedAt);

// ［2026-10-09 / #1845］登録の応答（201）。`McpClientView` の項目に `ClientSecret` を足したもの。
// 🔴 計画 ADR-0134 決定 2 の 1: **無人（service-account）の登録のときだけ** IdP（Keycloak）が生成した client secret を載せる（有人は null）。
//   これが値を見られる最初の機会であり、再表示の手段は持たない（次は再発行）。**登録簿・一覧・個別の応答には secret を持たない**
//   （プラットフォームは値を保存しない）。応答は `Cache-Control: no-store` で返す。
// 🔴 record の既定の `ToString` は全項目を書き出すので、値を出さない形へ差し替える（決定 2 の 2）。
public sealed record McpClientRegistrationView(
    Guid Id,
    string ClientId,
    string DisplayName,
    string Kind,
    bool Enabled,
    IReadOnlyDictionary<string, string> Attributes,
    string EgressTier,
    DateTimeOffset RegisteredAt,
    DateTimeOffset UpdatedAt,
    string? ClientSecret)
{
    internal static McpClientRegistrationView From(McpClientView view, Domain.Ports.ClientSecret? secret) => new(
        view.Id, view.ClientId, view.DisplayName, view.Kind, view.Enabled, view.Attributes, view.EgressTier,
        view.RegisteredAt, view.UpdatedAt, secret?.Reveal());

    public override string ToString()
        => $"McpClientRegistrationView {{ ClientId = {ClientId}, Kind = {Kind}, ClientSecret = {(ClientSecret is null ? "(なし)" : "(秘匿)")} }}";
}

// ［2026-10-09 / #1845］再発行の応答（200）。計画 ADR-0134 決定 2 の 5: IdP で再生成した新しい secret を一度だけ返す（旧 secret はその時点で失効）。
public sealed record McpClientSecretView(string ClientId, string ClientSecret)
{
    public override string ToString() => $"McpClientSecretView {{ ClientId = {ClientId}, ClientSecret = (秘匿) }}";
}

// SC-12「公開ツール一覧の確認」。実効ツール（申告 ∩ 公開構成）と構成ドリフトを返す。
public sealed record PublishedToolView(
    string Name,
    string Service,
    string Description,
    string RequiredScope,
    string EgressClass);

public sealed record EffectiveToolsView(
    int Version,
    IReadOnlyList<PublishedToolView> Tools,
    IReadOnlyList<ToolDriftView> Drifts);

public sealed record ToolDriftView(string Kind, string Target, string Detail);
