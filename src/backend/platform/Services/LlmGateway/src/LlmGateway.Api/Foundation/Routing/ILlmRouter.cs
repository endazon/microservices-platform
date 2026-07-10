namespace LlmGateway.Api.Foundation.Routing;

// FR-11, ADR-0010: LLM 呼び出し先の切り替え判定。
public interface ILlmRouter
{
    RoutingDecision Route(RoutingRequest request);
}

// 判定入力: 入力文書の最高機密区分・用途・（任意の）要求モデル。
public record RoutingRequest(SensitivityClass Sensitivity, string Purpose, string? RequestedModel = null);

// 判定結果: 送信可否・選択エンドポイント/ティア/モデル・要承認・理由（監査用）。
public record RoutingDecision(
    bool Allowed,
    string? EndpointName,
    string? Provider,
    ProtectionTier? Tier,
    string? Model,
    bool RequiresApproval,
    string Reason);
