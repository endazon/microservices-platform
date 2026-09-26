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

        // FR-03, ADR-0016, ADR-0017, IADR-0422 (#336): 検索クエリ専用の送信先固定（測定用の切替口）。
        //
        // 🔴 **絞り込みは越境判定の「後」に効く。** 候補は既に `allowedTiers` と `Enabled` の篩を
        // 通っているので、プロファイルは候補を**狭めることしかできない** —— 機密区分が許さない
        // ティアをここで開くことはできない。**前へ移すと越境の穴になる**（変異試験 M-4）。
        var profile = request.Purpose == EmbeddingRoutePurpose.Query
            ? _options.QueryProfile
            : null;

        // FR-03, ADR-0092 決定 2, [[IADR-0467]] (#336): 検索が名乗った読み先コレクションへの絞り込み。
        //
        // ADR-0092 決定 1 はコレクションごとに検索して束ねる。**ティア A のコレクションはティア A の
        // モデルで埋めたクエリでしか引けない**（次元も空間も違う）ので、検索側は束ねる追加コレクションの
        // 名前を要求に載せてくる。
        //
        // 🔴 **プロファイルと同じ位置（篩の後）に置く。** 候補は既に越境判定と `Enabled` を通っており、
        // ここは**狭めることしかできない** —— 前へ移すと無効なエンドポイントを名指しで選べる（変異 M-3）。
        // 🔴 **Index には効かせない。** 文書の送信先を呼び出し側が選べてはならない（変異 M-4）。
        // 🔴 **プロファイルとは積で効く**（両方が指定されれば両方を満たすものだけ）。片方で他方を
        // 上書きしない —— 絞り込みは積み重なるだけで、広げる経路を作らない。
        var targetCollection = request.Purpose == EmbeddingRoutePurpose.Query
            ? request.TargetCollection
            : null;

        var endpoint = _options.Endpoints
            .Where(e => e.Enabled && allowedTiers.Contains(e.Tier))
            .Where(e => string.IsNullOrWhiteSpace(profile) || e.Name == profile)
            .Where(e => string.IsNullOrWhiteSpace(targetCollection) || e.Collection == targetCollection)
            .OrderBy(e => e.Priority)   // 優先度（小さいほど優先）
            .ThenBy(e => e.Tier)        // 同順位はより保護の強いティアを優先（A<B<C）
            .FirstOrDefault();

        if (endpoint is null)
        {
            // 08_data-egress-policy: 許容ティアに送信可能なエンドポイントが無い場合は送信しない（fail-closed）。
            //
            // 🔴 プロファイル指定で候補が消えたときも**既定へ落とさない**（拒否する）。黙って voyage へ
            // 落ちると、Ruri を測ったつもりで voyage を測ることになる。通常の構成では
            // `EmbeddingRoutingOptionsValidator` が起動時に落とすので、ここへ来るのは
            // 「指定したエンドポイントが機密区分で許されない」場合だけである。
            var profileNote = string.IsNullOrWhiteSpace(profile)
                ? string.Empty
                : $"（クエリ送信先の固定 '{profile}' に該当する候補が無い）";
            // FR-03, [[IADR-0467]] (#336): 読み先コレクションの指定で候補が消えたときも既定へ落とさない。
            // 黙って別のコレクションのモデルで埋めると、検索側の照合が捨てるまで気づけない。
            if (!string.IsNullOrWhiteSpace(targetCollection))
                profileNote += $"（読み先コレクション '{targetCollection}' に該当する候補が無い）";
            var denyReason =
                $"機密区分 {request.Sensitivity}（用途 {request.Purpose}）は許容ティア {Format(allowedTiers)} に" +
                $"送信可能な埋め込みエンドポイントが無いため送信を拒否（fail-closed）{profileNote}";
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
