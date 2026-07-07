namespace KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;

// FR-15: 構成情報 API（BFF）が集約対象とするサービスの自己申告エンドポイント設定。
// 構成キー（pipeline.json の service 名）→ ベース URL。GitOps（Helm values）で注入する。
public sealed class IntrospectionOptions
{
    public const string SectionName = "Introspection";

    // service 名 → 自己申告エンドポイントのベース URL（例: "document-service": "http://document-service:5001"）。
    public Dictionary<string, string> Services { get; set; } = new(StringComparer.Ordinal);

    // 自己申告エンドポイントのパス（既定は /internal/introspection）。
    public string Path { get; set; } = IntrospectionExtensions.IntrospectionPath;

    // 収集の HTTP タイムアウト（秒）。到達不能判定の上限。
    public int TimeoutSeconds { get; set; } = 5;
}

// FR-15: 構成バージョン（適用中の構成定義の Git コミット ID・適用日時・適用者）。
// GitOps（ArgoCD）適用時に環境変数/構成として注入する。
public sealed class ConfigVersionOptions
{
    public const string SectionName = "Config";

    public string? GitCommit { get; set; }
    public string? AppliedAt { get; set; }
    public string? AppliedBy { get; set; }
}

// FR-15: ドリフト定期検出の設定。
public sealed class DriftDetectionOptions
{
    public const string SectionName = "Drift";

    // 定期検出の有効/無効（テスト・ローカルでは無効化して雑音を避ける）。
    public bool Enabled { get; set; } = true;

    // 検出間隔（秒）。既定 300 秒（5 分）＝計画の推奨間隔。
    public int IntervalSeconds { get; set; } = 300;
}
