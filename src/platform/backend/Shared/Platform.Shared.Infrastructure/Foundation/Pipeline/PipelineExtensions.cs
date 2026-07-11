using System.Text;
using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Foundation.Pipeline;

// FR-14, ADR-0018: 宣言的パイプライン構成の読み込みと、構成からの MassTransit トポロジ生成。
// 誤構成対策（10_composability-design.md §5 安全弁）として、宣言と実装の不整合は起動時に fail-fast する。
public static class PipelineExtensions
{
    // `Pipeline:ConfigPath` が指す JSON を構成へ追加する。未指定・不存在なら何もしない
    // （ローカル・テストは appsettings の Pipeline セクションまたは既定配線で動作する）。
    //
    // 供給形式は 2 種を許容する（FR-15 / #146）:
    //   1) {"Pipeline": {...}} オーバレイ（Helm ConfigMap が pipeline.json から生成する形）→ そのまま読む。
    //   2) 生の pipeline.json（トップレベル {"steps":..,"events":..}。compose は正の pipeline.json を
    //      直接マウントする）→ "Pipeline" セクションへ包んで読む。正の pipeline.json を複製せず
    //      単一の真実源のまま BFF/段ホストへ供給できる（compose でも宣言突合が正しく働く）。
    public static IHostApplicationBuilder AddPlatformPipelineConfig(
        this IHostApplicationBuilder builder)
    {
        var path = builder.Configuration["Pipeline:ConfigPath"];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return builder;
        }

        var json = File.ReadAllText(path);
        if (HasPipelineSection(json))
        {
            builder.Configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
        }
        else
        {
            // 生の pipeline.json を "Pipeline" セクションへ包む。
            var wrapped = $"{{\"{PipelineOptions.SectionName}\":{json}}}";
            builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(wrapped)));
        }
        return builder;
    }

    // ファイルが {"Pipeline": {...}} 形（オーバレイ）か否かを判定する。解析不能は AddJsonFile 側の
    // 明確なエラーに委ねるため true（従来動作）を返す。
    private static bool HasPipelineSection(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(PipelineOptions.SectionName, out _);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    public static PipelineOptions GetPlatformPipeline(this IConfiguration configuration)
    {
        var options = new PipelineOptions();
        configuration.GetSection(PipelineOptions.SectionName).Bind(options);
        return options;
    }

    // 段（コンシューマ）を宣言的構成に従って登録する。登録規則:
    //   1. 宣言なし（Steps 空）→ 既定で登録（現行配線と等価）
    //   2. 宣言があり段が未宣言 → 起動失敗（適用漏れ・名称ずれの検出）
    //   3. consumer 型完全名の不一致 → 起動失敗（段名の付け替え誤り検出）
    //   4. IConsumer<TIn> の TIn 型名と input の不一致 → 起動失敗（配線ずれ検出）
    //   5. enabled: false → 登録しない（購読・キューを生成しない）
    //   6. enabled: true → 登録（queue 指定時は受信エンドポイント名を上書き）
    public static void AddPlatformPipelineStep<TConsumer>(
        this IBusRegistrationConfigurator bus, PipelineOptions pipeline, ILogger? logger = null)
        where TConsumer : class, IConsumer, IPipelineStep
    {
        var stepName = TConsumer.StepName;

        if (pipeline.Steps.Count == 0)
        {
            logger?.LogInformation(
                "Pipeline config absent; step {Step} ({Consumer}) enabled by default",
                stepName, typeof(TConsumer).Name);
            bus.AddConsumer<TConsumer>();
            return;
        }

        var step = pipeline.FindStep(stepName)
            ?? throw new InvalidOperationException(
                $"パイプライン構成に段 '{stepName}' が宣言されていません（pipeline.json の steps を確認）。");

        // 宣言がある以上、照合対象（consumer/input）は必須。空なら CI 検証（V1）をすり抜けた
        // 経路（手書き appsettings 等）であり、照合スキップではなく起動失敗にする（二重の安全弁）。
        var consumerType = typeof(TConsumer).FullName!;
        if (string.IsNullOrEmpty(step.Consumer))
        {
            throw new InvalidOperationException(
                $"段 '{stepName}' の consumer 宣言が空です（pipeline.json の steps を確認）。");
        }
        if (!string.Equals(step.Consumer, consumerType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"段 '{stepName}' の consumer 宣言 '{step.Consumer}' が実装 '{consumerType}' と一致しません。");
        }

        var inputType = typeof(TConsumer).GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
            ?.GetGenericArguments()[0];
        if (string.IsNullOrEmpty(step.Input))
        {
            throw new InvalidOperationException(
                $"段 '{stepName}' の input 宣言が空です（pipeline.json の steps を確認）。");
        }
        if (inputType is not null
            && !string.Equals(step.Input, inputType.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"段 '{stepName}' の input 宣言 '{step.Input}' が実装の購読イベント '{inputType.Name}' と一致しません。");
        }

        if (!step.Enabled)
        {
            logger?.LogWarning(
                "Pipeline step {Step} ({Consumer}) is disabled by configuration; " +
                "no subscription or queue will be created", stepName, typeof(TConsumer).Name);
            return;
        }

        var registration = bus.AddConsumer<TConsumer>();
        if (!string.IsNullOrWhiteSpace(step.Queue))
        {
            registration.Endpoint(e => e.Name = step.Queue);
        }
        logger?.LogInformation(
            "Pipeline step {Step} registered: input={Input} queue={Queue}",
            stepName, step.Input, step.Queue ?? "(default)");
    }
}
