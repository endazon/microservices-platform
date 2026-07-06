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
        // CodeQL(log-forging): purpose は呼び出し側（利用者）由来の自由文字列のため、ログ出力前に
        // 改行・制御文字を除去してログ行の偽造を防ぐ（Sanitize）。Sensitivity は enum のため安全。
        var loggedPurpose = Sanitize(request.Purpose);

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
                request.Sensitivity, loggedPurpose, Format(allowedTiers));
            return new RoutingDecision(false, null, null, null, null, false, denyReason);
        }

        // IADR-0022: 候補を優先度順に走査し、適格モデル（ZDR 要件等を満たす）を持つ最初の
        // エンドポイントを採用する。先頭候補が ZDR 非対応モデルしか持たない場合でも、後続候補に
        // 適格モデルがあればそちらを使う（先頭候補だけを見て誤って拒否しない）。
        LlmEndpointOptions? endpoint = null;
        string model = string.Empty;
        foreach (var candidate in candidates)
        {
            var resolved = ResolveModel(candidate, request);
            if (!string.IsNullOrEmpty(resolved))
            {
                endpoint = candidate;
                model = resolved;
                break;
            }
        }

        if (endpoint is null)
        {
            // IADR-0022: いずれの候補エンドポイントにも ZDR 対応モデルが無い場合は送信しない（安全側で拒否）。
            var denyReason = $"機密区分 {request.Sensitivity} は ZDR 対応モデルを持つ送信可能なエンドポイントが無いため送信を拒否";
            logger.LogWarning("LLM routing denied: sensitivity={Sensitivity} purpose={Purpose} reason=no-zdr-model",
                request.Sensitivity, loggedPurpose);
            return new RoutingDecision(false, null, null, null, null, false, denyReason);
        }

        var requiresApproval = EgressMatrix.RequiresApproval(request.Sensitivity, endpoint.Tier);
        var reason = $"機密区分 {request.Sensitivity} / 用途 {request.Purpose} → ティア{endpoint.Tier} {endpoint.Name}";

        // ADR-0010: 送信判定を監査ログへ記録する。
        logger.LogInformation(
            "LLM routing decision: sensitivity={Sensitivity} purpose={Purpose} endpoint={Endpoint} tier={Tier} model={Model} requiresApproval={RequiresApproval}",
            request.Sensitivity, loggedPurpose, endpoint.Name, endpoint.Tier, model, requiresApproval);

        return new RoutingDecision(true, endpoint.Name, endpoint.Provider, endpoint.Tier, model, requiresApproval, reason);
    }

    // CodeQL(cs/log-forging): ログに出力する利用者由来の文字列から改行・制御文字を除去する。
    // 改行を注入したログ行の偽造（forged log entries）を防ぐ。
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return new string(Array.ConvertAll(value.ToCharArray(), c => char.IsControl(c) ? '_' : c));
    }

    // 用途→モデルを解決する。明示要求モデルがエンドポイント対応なら優先、次に用途別モデル、最後に既定。
    // IADR-0022: ZDR を要件とする機密区分（confidential/restricted）では、ZDR 非対応モデル
    // （endpoint.NonZdrModels）を候補から除外し、ZDR 対応モデルへフォールバックする。
    // 適格モデルが 1 つも無ければ空文字を返し、呼び出し側で送信拒否へ縮退させる。
    private string ResolveModel(LlmEndpointOptions endpoint, RoutingRequest request)
    {
        var eligible = EligibleModels(endpoint, request.Sensitivity);

        if (!string.IsNullOrWhiteSpace(request.RequestedModel)
            && eligible.Contains(request.RequestedModel))
            return request.RequestedModel!;

        if (_options.PurposeModels.TryGetValue(request.Purpose, out var purposeModel)
            && eligible.Contains(purposeModel))
            return purposeModel;

        if (!string.IsNullOrWhiteSpace(endpoint.DefaultModel) && eligible.Contains(endpoint.DefaultModel))
            return endpoint.DefaultModel;

        return eligible.FirstOrDefault() ?? string.Empty;
    }

    // IADR-0022: ZDR 要件のある機密区分では ZDR 非対応モデルを除外した適格モデル一覧を返す。
    // 要件が無い区分（public/internal）や NonZdrModels 未設定なら Models をそのまま返す。
    private static IReadOnlyList<string> EligibleModels(LlmEndpointOptions endpoint, SensitivityClass sensitivity)
    {
        if (endpoint.NonZdrModels.Count == 0 || !EgressMatrix.RequiresZeroDataRetention(sensitivity))
            return endpoint.Models;

        return endpoint.Models
            .Where(m => !endpoint.NonZdrModels.Contains(m, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static string Format(IReadOnlySet<ProtectionTier> tiers)
        => string.Join(",", tiers.OrderBy(t => t));
}
