using Microsoft.Extensions.Options;

namespace LlmGateway.Domain.Routing;

// FR-02, FR-05, ADR-0016, ADR-0017: 埋め込み専用の越境マトリクス（EmbeddingEgress）に基づき、
// 埋め込み送信先（エンドポイント・モデル・次元・コレクション）を選択、または送信を拒否する。
//
// fail-closed の要点: confidential/restricted はティアA（セルフホスト）のみ許容し、
// セルフホストが無効（既定＝未構築）なら候補が無く送信を拒否する。外部（ティアB=voyage）は
// 高機密区分では決して候補にならないため、本文が外部埋め込み API へ送信されることはない。
public sealed class EmbeddingRouter(IOptions<EmbeddingRoutingOptions> options, ILogger<EmbeddingRouter> logger)
    : IEmbeddingRouter
{
    private readonly EmbeddingRoutingOptions _options = options.Value;

    public EmbeddingRoutingDecision Route(EmbeddingRoutingRequest request)
    {
        // ADR-0016: 検索クエリは検索対象コレクション（既定 voyage/1024）へ整合させるため、
        // 機密区分に依らず既定外部経路（ティアB）へ固定する（public 相当として扱う）。
        // 取り込み（Index）は機密区分どおりに越境判定する。
        var sensitivity = request.Purpose == EmbeddingRoutePurpose.Query
            ? SensitivityClass.Public
            : request.Sensitivity;

        var allowedTiers = EmbeddingEgress.AllowedTiers(sensitivity);

        var endpoint = _options.Endpoints
            .Where(e => e.Enabled && allowedTiers.Contains(e.Tier))
            .OrderBy(e => e.Priority)   // 優先度（小さいほど優先）
            .ThenBy(e => e.Tier)        // 同順位はより保護の強いティアを優先（A<B<C）
            .FirstOrDefault();

        if (endpoint is null)
        {
            // 08_data-egress-policy: 許容ティアに送信可能なエンドポイントが無い場合は送信しない（fail-closed）。
            var denyReason =
                $"機密区分 {request.Sensitivity}（用途 {request.Purpose}）は許容ティア {Format(allowedTiers)} に" +
                "送信可能な埋め込みエンドポイントが無いため送信を拒否（fail-closed）";
            logger.LogWarning(
                "Embedding routing denied: sensitivity={Sensitivity} purpose={Purpose} allowedTiers={AllowedTiers}",
                request.Sensitivity, request.Purpose, Format(allowedTiers));
            return new EmbeddingRoutingDecision(false, null, null, null,
                string.Empty, 0, string.Empty, denyReason);
        }

        var reason =
            $"機密区分 {request.Sensitivity} / 用途 {request.Purpose} → ティア{endpoint.Tier} {endpoint.Name}" +
            $"（{endpoint.Model}, {endpoint.Dimensions}次元, {endpoint.Collection}）";

        // ADR-0016: 送信判定を監査ログへ記録する（機密区分・ティア・モデル・コレクション）。
        logger.LogInformation(
            "Embedding routing decision: sensitivity={Sensitivity} purpose={Purpose} endpoint={Endpoint} tier={Tier} model={Model} dimensions={Dimensions} collection={Collection}",
            request.Sensitivity, request.Purpose, endpoint.Name, endpoint.Tier, endpoint.Model,
            endpoint.Dimensions, endpoint.Collection);

        return new EmbeddingRoutingDecision(true, endpoint.Name, endpoint.Provider, endpoint.Tier,
            endpoint.Model, endpoint.Dimensions, endpoint.Collection, reason);
    }

    private static string Format(IReadOnlySet<ProtectionTier> tiers)
        => string.Join(",", tiers.OrderBy(t => t));
}
