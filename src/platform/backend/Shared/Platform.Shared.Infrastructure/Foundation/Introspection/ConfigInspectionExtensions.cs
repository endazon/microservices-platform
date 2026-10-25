using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Platform.Shared.Infrastructure.Foundation.Introspection;

// FR-15: 構成情報 API（集約・ドリフト検出・監査）の依存を一括登録する。
// 実装配置は独立サービス化せず既存の合成ホスト（BFF）へ同居させる（過剰分割回避。IADR に記録）。
public static class ConfigInspectionExtensions
{
    public static IHostApplicationBuilder AddPlatformConfigInspection(
        this IHostApplicationBuilder builder)
    {
        // 宣言（pipeline.json）を読み込み、突合の基準として共有する。
        builder.AddPlatformPipelineConfig();
        var pipeline = builder.Configuration.GetPlatformPipeline();
        EnsureDeclarationUsable(builder.Configuration, pipeline);
        builder.Services.AddSingleton(pipeline);

        builder.Services.Configure<IntrospectionOptions>(
            builder.Configuration.GetSection(IntrospectionOptions.SectionName));
        builder.Services.Configure<ConfigVersionOptions>(
            builder.Configuration.GetSection(ConfigVersionOptions.SectionName));
        builder.Services.Configure<DriftDetectionOptions>(
            builder.Configuration.GetSection(DriftDetectionOptions.SectionName));

        builder.Services.TryAddSingletonTimeProvider();

        // FR-15, NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 4, IADR-0462, [[IADR-0533]] 決定 3 (#1514, #1517):
        // 自己申告は gRPC（h2c）だけで収集する（REST `GET /internal/introspection` の収集は撤去した）。
        // 資格情報は BFF 自身の s2s トークン（`ServiceToken:*`）。宛先は `Introspection:Services`（値は gRPC の宛先）。
        EnsureRetiredKeyAbsent(builder.Configuration);
        builder.Services.AddPlatformServiceToken(builder.Configuration);
        builder.Services.AddSingleton<GrpcServiceIntrospectionCollector>();
        builder.Services.AddSingleton<IEffectiveConfigCollector, EffectiveConfigCollector>();
        builder.Services.AddSingleton<IConfigInspectionService, ConfigInspectionService>();
        builder.Services.AddSingleton<IDriftAlertSink, LoggingDriftAlertSink>();
        builder.Services.AddSingleton<IAuditLogger, AuditLogger>();

        // FR-15 (#145): 定期検出と適用直後の即時検出（PostSync 起動）が共有する単一実行経路。
        builder.Services.AddSingleton<IDriftRunner, DriftRunner>();

        // 定期ドリフト検出（既定 5 分）。Drift:Enabled=false で無効化できる。
        builder.Services.AddHostedService<DriftDetectionHostedService>();

        return builder;
    }

    // FR-15 (#444): 突合の基準そのものが空のまま起動することを止める。
    //
    // 🔴 **宣言の読み込みは「指定はあるが読めなかった」を黙って許す。** `AddPlatformPipelineConfig` は
    // `Pipeline:ConfigPath` が指すファイルが無ければ何もせずに返る（段ホストがローカルで既定配線に
    // 縮退できるようにするための仕様）。構成情報 API 側でそれが起きると、**宣言 0 件が突合の基準になり、
    // 実効の購読すべてが `UndeclaredSubscription` として偽陽性で並ぶ** —— #146 / #118 監査で実際に
    // 起きた回帰である。当時の是正は compose / Helm のマウント配線を静的に検査するものであり、
    // **読む側には防壁が無かった**。宣言を要求しておきながら受け取れていないなら起動させない。
    //
    // ConfigPath 未指定（ローカル・単体試験）は従来どおり素通りする（宣言なしは正当な構成である）。
    private static void EnsureDeclarationUsable(IConfiguration configuration, PipelineOptions pipeline)
    {
        var path = configuration[$"{PipelineOptions.SectionName}:ConfigPath"];
        if (string.IsNullOrWhiteSpace(path) || pipeline.Steps.Count > 0)
            return;

        throw new InvalidOperationException(
            $"構成情報 API はパイプライン宣言を突合の基準として要求しますが、'{path}' から段を 1 件も"
            + " 読み込めませんでした（マウント漏れ・空ファイル・形式違いの疑い）。宣言 0 件のまま起動すると"
            + " 実効の全購読が UndeclaredSubscription として誤報されるため、起動を止めます。");
    }

    // ［2026-10-10 / #1517・[[IADR-0533]] 決定 3］🔴 **撤去した旧キー `Introspection:GrpcServices` が残っていたら起動を止める。**
    // 構成キーは `Introspection:Services` へ一本化した（値は gRPC の宛先）。旧キーを黙って無視すると、
    // 上書き値（helm の `extraEnv`・compose・手元の構成）の移し忘れが、収集の時点で「到達不能」としてしか現れない
    // （旧 `Introspection:Services` の REST の宛先 `:8080` は h2c を話さない）。起動の時点で落として気づかせる。
    private static void EnsureRetiredKeyAbsent(IConfiguration configuration)
    {
        var stale = configuration.GetSection(IntrospectionOptions.RetiredGrpcServicesSection).GetChildren()
            .Select(c => c.Key).ToList();
        if (stale.Count == 0)
            return;

        throw new InvalidOperationException(
            $"構成キー {IntrospectionOptions.RetiredGrpcServicesSection} は撤去しました（REST の並走の退役。#1517）。"
            + $" gRPC の宛先は {IntrospectionOptions.SectionName}:Services へ移してください（値は h2c のアドレス。例: http://document-service:8081）。"
            + $" 残っている項目: {string.Join(", ", stale)}");
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeProvider)))
            services.AddSingleton(TimeProvider.System);
    }
}
