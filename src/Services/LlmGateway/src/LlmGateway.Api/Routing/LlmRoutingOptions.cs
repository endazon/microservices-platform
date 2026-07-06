namespace LlmGateway.Api.Routing;

// FR-11, ADR-0010: LLM 呼び出し先（エンドポイント）の設定駆動定義。
// 契約改定やティア再判定に運用で追従できるよう、コードでなく設定で定義する。
public sealed class LlmRoutingOptions
{
    public const string SectionName = "Llm:Routing";

    // 呼び出し先エンドポイント一覧。
    public List<LlmEndpointOptions> Endpoints { get; set; } = [];

    // 用途（purpose）→ 既定モデル。用途別にコスト・品質を最適化する（ADR-0010 / IADR-0022）。
    // キーは呼び出し側が送る purpose 値と一致させる（例: rag-answer, analysis, diagram-coding）。
    // 既定 claude-opus-4-8 / 定型 rag-answer→claude-sonnet-4-6・diagram-coding→claude-haiku-4-5 /
    // 最難関 analysis→claude-fable-5。
    public Dictionary<string, string> PurposeModels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // internal × ティアC（要承認）を自動許可するか。既定は安全側で false（承認が無ければ C を使わない）。
    public bool AllowUnapprovedTierC { get; set; }
}

public sealed class LlmEndpointOptions
{
    // 表示・監査用のエンドポイント名。
    public string Name { get; set; } = string.Empty;

    // データ保護ティア（A/B/C）。
    public ProtectionTier Tier { get; set; }

    // 呼び出しに用いるプロバイダ実装のキー（keyed DI: "claude" / "selfhosted"）。
    public string Provider { get; set; } = string.Empty;

    // このエンドポイントが提供するモデル一覧。
    public List<string> Models { get; set; } = [];

    // ZDR（ゼロデータ保持）非対応のモデル一覧（08_data-egress-policy の注意点 / IADR-0022）。
    // 同一ティア内でもモデルによりデータ保持条件が異なるため、ここに列挙したモデルは ZDR を要件とする
    // 機密区分（confidential/restricted）では候補から除外する。既定は空（=全モデル ZDR 対応扱い）。
    public List<string> NonZdrModels { get; set; } = [];

    // 用途別モデルが未指定・非対応のときのフォールバックモデル。
    public string DefaultModel { get; set; } = string.Empty;

    // 無効化されたエンドポイントは候補から除外する（例: 未構築のセルフホスト基盤）。
    public bool Enabled { get; set; } = true;

    // 候補が複数あるときの優先度。小さいほど優先。
    public int Priority { get; set; } = 100;
}
