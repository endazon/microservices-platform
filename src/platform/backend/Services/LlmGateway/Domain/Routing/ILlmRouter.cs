namespace LlmGateway.Domain.Routing;

// FR-11, ADR-0010: LLM 呼び出し先の切り替え判定。
public interface ILlmRouter
{
    RoutingDecision Route(RoutingRequest request);
}

// 判定入力: 入力文書の最高機密区分・用途・（任意の）要求モデル。
public record RoutingRequest(SensitivityClass Sensitivity, string Purpose, string? RequestedModel = null);

// 判定結果: 送信可否・選択エンドポイント/ティア/モデル・要承認・理由（監査用）。
// ADR-0038 決定 3 (#863): FallbackModels は「第 1 候補（Model）が HTTP 400 系で失敗したときに
// 順に試すモデル」である。ルーター側で適格性（Models 登録・ZDR 除外）を通した後の値だけが載る。
// 既定 null は「鎖なし＝フォールバックしない」を意味する（呼び出し側は Fallbacks で受け取る）。
public record RoutingDecision(
    bool Allowed,
    string? EndpointName,
    string? Provider,
    ProtectionTier? Tier,
    string? Model,
    bool RequiresApproval,
    string Reason,
    IReadOnlyList<string>? FallbackModels = null)
{
    // 呼び出し側に null と空の区別をさせない（鎖が無いことは異常ではなく既定である）。
    public IReadOnlyList<string> Fallbacks => FallbackModels ?? [];
}
