namespace KnowledgePlatform.Shared.Contracts.Dtos;

// FR-02, FR-03, FR-05, ADR-0013, ADR-0016, ADR-0017: LLM ゲートウェイ /embed の要求・応答契約。
// LlmGateway（実装側）と IngestionService / RetrievalService（呼び出し側）で二重管理せず、
// 契約変更時の追従漏れ（ドリフト）を防ぐため共有コントラクトに一元化する（/complete と同方針）。

// 埋め込み生成の用途。取り込み（index）は文書本文を送るため機密区分で越境判定・fail-closed。
// 検索クエリ（query）は検索対象コレクション（既定 voyage/1024）へ整合させるため既定外部経路へ固定する。
public enum EmbedPurpose
{
    Index = 0,
    Query = 1
}

// FR-05, ADR-0016: Confidentiality（文書/入力の機密区分）で送信先ティア・モデル・コレクションを切り替える。
//   埋め込みは本文全量を送信するため、confidential/restricted はティアA（セルフホスト）固定で外部送信しない。
//   Purpose=Query の検索クエリは既定外部経路（voyage）へ固定する（検索対象コレクションと次元を一致させる）。
public record EmbedApiRequest(
    string Text,
    string? Confidentiality = null,
    EmbedPurpose Purpose = EmbedPurpose.Index);

// FR-02, ADR-0016: Embedded=false は機密区分による送信拒否（fail-closed）または次元不整合・呼び出し失敗を示す。
//   呼び出し側（Ingestion）は Embedded=false のとき索引をスキップする。
//   Collection は索引/検索対象のモデル別コレクション名（例 knowledge_chunks_voyage_3_5 / _ruri_v3）。
//   Endpoint / RoutingReason は選択・拒否した送信先と理由（監査・縮退表示用）。
public record EmbedApiResponse(
    float[] Vector,
    int Dimensions,
    string Model,
    string Collection,
    bool Embedded = true,
    string? Endpoint = null,
    string? RoutingReason = null);
