namespace LlmGateway.Domain.Routing;

// FR-02, FR-05, ADR-0016: 機密区分と用途から埋め込み送信先を決定する（または送信を拒否する）。
public interface IEmbeddingRouter
{
    EmbeddingRoutingDecision Route(EmbeddingRoutingRequest request);
}

// Purpose=Index は取り込み（文書本文）で機密区分により越境判定・fail-closed。
// Purpose=Query は検索クエリで、検索対象コレクション（既定 voyage/1024）へ整合させるため既定外部経路へ固定する。
public enum EmbeddingRoutePurpose
{
    Index = 0,
    Query = 1
}

public record EmbeddingRoutingRequest(SensitivityClass Sensitivity, EmbeddingRoutePurpose Purpose);

// Allowed=false は送信拒否（fail-closed）。呼び出し側は索引をスキップする。
public record EmbeddingRoutingDecision(
    bool Allowed,
    string? EndpointName,
    string? Provider,
    ProtectionTier? Tier,
    string Model,
    int Dimensions,
    string Collection,
    string Reason);
