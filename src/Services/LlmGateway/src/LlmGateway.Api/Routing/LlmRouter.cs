using Microsoft.Extensions.Options;

namespace LlmGateway.Api.Routing;

// FR-11, ADR-0010, 08_data-egress-policy: 機密区分×ティアの越境マトリクスと用途に基づき、
// LLM 呼び出し先（エンドポイント）とモデルを選択、または送信を拒否する。
public sealed class LlmRouter(IOptions<LlmRoutingOptions> options, ILogger<LlmRouter> logger) : ILlmRouter
{
    private readonly LlmRoutingOptions _options = options.Value;

    public RoutingDecision Route(RoutingRequest request)
    {
        var allowedTiers = EgressMatrix.AllowedTiers(request.Sensitivity);

        // 許容ティアに属し、有効なエンドポイントのみを候補にする。
        // 要承認（internal×C）は、明示許可が無い限り候補から除外する（既定は安全側）。
        var candidates = _options.Endpoints
            .Where(e => e.Enabled && allowedTiers.Contains(e.Tier))
            .Where(e => _options.AllowUnapprovedTierC
                        || !EgressMatrix.RequiresApproval(request.Sensitivity, e.Tier))
            .OrderBy(e => e.Priority)   // 優先度（小さいほど優先）
            .ThenBy(e => e.Tier)        // 同順位はより保護の強いティアを優先（A<B<C）
            .ToList();

        if (candidates.Count == 0)
        {
            // 08_data-egress-policy: 許容ティアに送信可能なエンドポイントが無い場合は送信しない（縮退/拒否）。
            var denyReason = $"機密区分 {request.Sensitivity} は許容ティア {Format(allowedTiers)} に送信可能なエンドポイントが無いため送信を拒否";
            logger.LogWarning("LLM routing denied: sensitivity={Sensitivity} purpose={Purpose} allowedTiers={AllowedTiers}",
                request.Sensitivity, request.Purpose, Format(allowedTiers));
            return new RoutingDecision(false, null, null, null, null, false, denyReason);
        }

        var endpoint = candidates[0];
        var model = ResolveModel(endpoint, request);
        var requiresApproval = EgressMatrix.RequiresApproval(request.Sensitivity, endpoint.Tier);
        var reason = $"機密区分 {request.Sensitivity} / 用途 {request.Purpose} → ティア{endpoint.Tier} {endpoint.Name}";

        // ADR-0010: 送信判定を監査ログへ記録する。
        logger.LogInformation(
            "LLM routing decision: sensitivity={Sensitivity} purpose={Purpose} endpoint={Endpoint} tier={Tier} model={Model} requiresApproval={RequiresApproval}",
            request.Sensitivity, request.Purpose, endpoint.Name, endpoint.Tier, model, requiresApproval);

        return new RoutingDecision(true, endpoint.Name, endpoint.Provider, endpoint.Tier, model, requiresApproval, reason);
    }

    // 用途→モデルを解決する。明示要求モデルがエンドポイント対応なら優先、次に用途別モデル、最後に既定。
    private string ResolveModel(LlmEndpointOptions endpoint, RoutingRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.RequestedModel)
            && endpoint.Models.Contains(request.RequestedModel))
            return request.RequestedModel!;

        if (_options.PurposeModels.TryGetValue(request.Purpose, out var purposeModel)
            && endpoint.Models.Contains(purposeModel))
            return purposeModel;

        return string.IsNullOrWhiteSpace(endpoint.DefaultModel)
            ? endpoint.Models.FirstOrDefault() ?? string.Empty
            : endpoint.DefaultModel;
    }

    private static string Format(IReadOnlySet<ProtectionTier> tiers)
        => string.Join(",", tiers.OrderBy(t => t));
}
