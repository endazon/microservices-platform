using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ConversionService.Infrastructure.ExternalServices;

// FR-12, UC-06, ADR-0012, IADR-0317 決定 5 (#1097): pandoc が実行時イメージに在ることを
// readiness で確かめる。
//
// 🔴 **これが「無い状態を検知できる」ことの実物側の担保である。**
// pandoc の欠落は従前まったく現れなかった —— 変換は例外を出さずプレースホルダ本文へ落ち、
// ジョブは成功として並び、ヘルスチェックも緑だった。本チェックがあると、pandoc を持たない
// イメージを配ったとき Pod が **Ready にならない**ので、配る側が気づく。
//
// fail-closed を選んだ構成でだけ登録する（`AllowDegradedBodyConversion=true` の開発機では
// 縮退が正常な振る舞いなので、readiness を落とす理由が無い）。
public sealed class PandocHealthCheck(ILogger<PandocHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var version = await PandocConversionService.TryGetPandocVersionAsync(cancellationToken);
        if (version is not null)
            return HealthCheckResult.Healthy(version);

        logger.LogError(
            "pandoc が見つからない。本サービスの本文変換は pandoc を外部プロセスとして起動するため、"
            + "この状態では FR-12 の本文 Markdown 化ができない（実行時イメージへ pandoc を導入すること）。");
        return HealthCheckResult.Unhealthy(
            "pandoc not found in the runtime image; body conversion cannot run");
    }
}
