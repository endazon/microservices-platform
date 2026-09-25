namespace Platform.Shared.Infrastructure.Foundation.Introspection;

// FR-15: 構成情報 API（BFF）が集約対象とするサービスの自己申告エンドポイント設定。
// 構成キー（pipeline.json の service 名）→ ベース URL。GitOps（Helm values）で注入する。
public sealed class IntrospectionOptions
{
    public const string SectionName = "Introspection";

    // service 名 → 自己申告エンドポイントのベース URL（例: "document-service": "http://document-service:5001"）。
    public Dictionary<string, string> Services { get; set; } = new(StringComparer.Ordinal);

    // FR-15, NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 5, IADR-0462 (#1514, #1255 経路 ⑤):
    // service 名 → 自己申告の **gRPC（h2c）アドレス**（例: "document-service": "http://document-service:8081"）。
    // 🔴 **宛先ごとの opt-in である。** ここに在る宛先だけが gRPC で収集され、無い宛先は上の
    // `Services` の REST のまま（並走中の正は REST。戻すのはこの 1 行を消すだけでよい）。
    // 両方に在れば gRPC を使う。値が空の項目は構成されていないものとして扱う。
    public Dictionary<string, string> GrpcServices { get; set; } = new(StringComparer.Ordinal);

    // 自己申告エンドポイントのパス（既定は /internal/introspection）。
    public string Path { get; set; } = IntrospectionExtensions.IntrospectionPath;

    // 収集の HTTP タイムアウト（秒）。到達不能判定の上限。
    // IADR-0462 (#1514): gRPC 収集の期限（deadline）も**同じ値を引く**（輸送ごとに別の値を持たない）。
    public int TimeoutSeconds { get; set; } = 5;

    // IADR-0462 (#1514): gRPC で収集する宛先（値が空の項目を除く）。
    // 構成の束縛（ConfigurationBinder）に拾われないよう、プロパティではなくメソッドにしてある。
    public IReadOnlyDictionary<string, string> ConfiguredGrpcServices() =>
        GrpcServices
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
}

// FR-15: 構成バージョン（適用中の構成定義の Git コミット ID・適用日時・適用者）。
// GitOps（ArgoCD）適用時に環境変数/構成として注入する。
public sealed class ConfigVersionOptions
{
    public const string SectionName = "Config";

    public string? GitCommit { get; set; }
    public string? AppliedAt { get; set; }
    public string? AppliedBy { get; set; }

    // FR-15 (#139), IADR-0046: 適用履歴（新しい順）。正データ源は GitOps 層（Git/ArgoCD の適用履歴）で、
    // 現在バージョンと同じ注入経路（GitOps→構成）で供給する。API 側は永続化しない（注入されたスライスを surfacing）。
    // 未注入（dev/compose）時は現在バージョンの単一エントリへ縮退する（ConfigInspectionService 参照）。
    public List<ConfigVersionHistoryEntryOptions> History { get; set; } = new();
}

// FR-15 (#139): 適用履歴 1 エントリの注入設定。HadDrift は注入時に判明していれば設定（不明なら未設定）。
public sealed class ConfigVersionHistoryEntryOptions
{
    public string? GitCommit { get; set; }
    public string? AppliedAt { get; set; }
    public string? AppliedBy { get; set; }
    public bool? HadDrift { get; set; }
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
