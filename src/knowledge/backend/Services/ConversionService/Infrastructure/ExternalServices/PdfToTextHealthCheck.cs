using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ConversionService.Infrastructure.ExternalServices;

// FR-12, UC-06, ADR-0070 決定 2, IADR-0362 決定 7 (#1192): pdftotext（poppler-utils）が実行時イメージに
// 在ることを readiness で確かめる。`PandocHealthCheck`（IADR-0320 決定 5）と同型である。
//
// 🔴 **これが「無い状態を検知できる」ことの実物側の担保である。** 抽出器を持たないイメージを配ると
// Pod が **Ready にならない**ので、配る側が気づく。fail-closed を選んだ構成でだけ登録する
// （`AllowDegradedBodyConversion=true` の開発機では縮退が正常な振る舞いなので、readiness を落とす理由が無い）。
public sealed class PdfToTextHealthCheck(ILogger<PdfToTextHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var version = await PdfTextLayerConverter.TryGetPdfToTextVersionAsync(cancellationToken);
        if (version is not null)
            return HealthCheckResult.Healthy(version);

        logger.LogError(
            "pdftotext が見つからない。PDF の本文抽出は pdftotext を外部プロセスとして起動するため、"
            + "この状態では PDF の本文 Markdown 化ができない（実行時イメージへ poppler-utils を導入すること）。");
        return HealthCheckResult.Unhealthy(
            "pdftotext not found in the runtime image; PDF body extraction cannot run");
    }
}
