using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Platform.Shared.Infrastructure.Foundation.Extensions;

public static class HealthCheckExtensions
{
    /// <summary>readiness（<c>/health/ready</c>）が拾うタグ。**自プロセスが要求を捌くのに必須な依存だけ**に付ける。</summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// NFR-06, ADR-0052 決定 4, [[IADR-0602]] (#2105): **下流サービスの疎通の報告**に付けるタグ。
    /// <c>/health/dependencies</c> だけが拾い、readiness・liveness には入らない。
    /// </summary>
    /// <remarks>
    /// 🔴 下流の疎通を <see cref="ReadyTag"/> で登録してはならない —— 下流 1 つの停止で呼び出し側の
    /// 全レプリカが NotReady になり、エッジから全画面が 503 になる（NFR-06 の縮退と逆向き）。
    /// </remarks>
    public const string DependenciesTag = "dependencies";

    public static IHealthChecksBuilder AddPlatformHealthChecks(
        this IServiceCollection services) =>
        services.AddHealthChecks();

    public static WebApplication MapPlatformHealthChecks(
        this WebApplication app)
    {
        // Liveness: プロセスが生きているか（依存不要）
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        });
        // Readiness: 自プロセスが要求を捌くのに必須な依存（自分のデータストア・ブローカ等）への疎通確認。
        // 下流サービスは入れない（DependenciesTag。IADR-0602）。
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = hc => hc.Tags.Contains(ReadyTag)
        });
        return app;
    }

    /// <summary>
    /// NFR-06, [[IADR-0602]] (#2105): 下流サービスの疎通の**報告口** <c>/health/dependencies</c> を張る（opt-in）。
    /// <see cref="DependenciesTag"/> の付いたチェックだけを拾い、**常に 200** で JSON（全体と下流ごとの状態）を返す。
    /// </summary>
    /// <remarks>
    /// probe ではない（helm は readiness / liveness にこの経路を使わない）。状態コードで縮退を表すと、
    /// 誤ってこの経路を probe に向けたときに #2105 の連鎖が戻るため、状態は本文だけで表す。
    /// 例外の文言は本文へ出さない（接続先・内部構成の露出を避ける。状態名だけ）。
    /// </remarks>
    public static WebApplication MapPlatformDependencyHealthChecks(
        this WebApplication app)
    {
        app.MapHealthChecks("/health/dependencies", new HealthCheckOptions
        {
            Predicate = hc => hc.Tags.Contains(DependenciesTag),
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status200OK,
            },
            ResponseWriter = WriteDependencyReportAsync,
        });
        return app;
    }

    private static Task WriteDependencyReportAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status.ToString(),
            entries = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }
}
