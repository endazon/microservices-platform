namespace LlmGateway.Domain.Routing;

// FR-02, ADR-0016, ADR-0017: 埋め込み送信先（エンドポイント）の設定駆動定義。
// 契約改定やモデル差し替え・再索引に運用で追従できるよう、コードでなく設定で定義する（IADR-0007 と同方針）。
public sealed class EmbeddingRoutingOptions
{
    public const string SectionName = "Embedding:Routing";

    // 埋め込み送信先エンドポイント一覧。
    public List<EmbeddingEndpointOptions> Endpoints { get; set; } = [];
}

public sealed class EmbeddingEndpointOptions
{
    // 表示・監査用のエンドポイント名。
    public string Name { get; set; } = string.Empty;

    // データ保護ティア（A=セルフホスト / B=保護契約済み外部 / C=標準外部）。
    public ProtectionTier Tier { get; set; }

    // 呼び出しに用いる埋め込みプロバイダ実装のキー（keyed DI: "voyage" / "selfhosted-embedding"）。
    public string Provider { get; set; } = string.Empty;

    // 埋め込みモデル名（例 voyage-3.5 / ruri-v3）。
    public string Model { get; set; } = string.Empty;

    // 埋め込み次元（voyage-3.5=1024 / ruri-v3=768 系）。索引・検索の整合に用いる。
    public int Dimensions { get; set; }

    // 索引/検索対象のモデル別 Qdrant コレクション名。異なるモデルのベクトルは同一空間で比較できないため
    // モデル別に分離する（ADR-0016）。例 knowledge_chunks_voyage_3_5 / knowledge_chunks_ruri_v3。
    public string Collection { get; set; } = string.Empty;

    // 無効化されたエンドポイントは候補から除外する（例: 未構築のセルフホスト基盤は既定 false＝高機密は fail-closed）。
    public bool Enabled { get; set; } = true;

    // 候補が複数あるときの優先度。小さいほど優先。
    public int Priority { get; set; } = 100;
}
