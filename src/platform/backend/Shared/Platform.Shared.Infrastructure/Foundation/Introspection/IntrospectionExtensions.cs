using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Shared.Infrastructure.Foundation.Introspection;

// FR-15, ADR-0018: 各サービス・段の自己申告（イントロスペクション）を組み立て、
// メッシュ内部限定のエンドポイントとして公開する。構成情報 API（BFF）はこれを集約して
// 実効構成を組み立て、宣言（pipeline.json）と突合してドリフトを検出する。
public static class IntrospectionExtensions
{
    // 自己申告エンドポイントの内部パス（ingress へは公開しない。メッシュ内部限定）。
    public const string IntrospectionPath = "/internal/introspection";

    // サービスの自己申告（購読/発行する段・選択中ポート・コネクタ）を構築して登録する。
    // 段の実効値（enabled・outputs）は宣言（pipeline）から解決する（登録規則と同じ導出）。
    public static IServiceCollection AddPlatformIntrospection(
        this IServiceCollection services,
        string service,
        PipelineOptions pipeline,
        Action<IntrospectionBuilder>? configure = null)
    {
        var builder = new IntrospectionBuilder(pipeline);
        configure?.Invoke(builder);
        var report = new ServiceIntrospectionDto(
            service, builder.Steps, builder.Ports, builder.Connectors);
        services.AddSingleton(report);
        return services;
    }

    // 自己申告エンドポイント（GET /internal/introspection）をマップする。
    // メッシュ内部限定（ネットワーク分離 IADR-0017 / mTLS IADR-0026 が防御）。ingress へは公開しない。
    public static IEndpointRouteBuilder MapPlatformIntrospection(this IEndpointRouteBuilder app)
    {
        app.MapGet(IntrospectionPath,
            (HttpContext ctx) => Results.Ok(
                ctx.RequestServices.GetRequiredService<ServiceIntrospectionDto>()))
           .WithName("PlatformIntrospection")
           .ExcludeFromDescription();
        return app;
    }
}

// 自己申告の内容（段・ポート・コネクタ）を宣言的に組み立てるビルダ。
public sealed class IntrospectionBuilder
{
    private readonly PipelineOptions _pipeline;

    internal List<StepIntrospectionDto> Steps { get; } = [];
    internal List<PortSelectionDto> Ports { get; } = [];
    internal List<ConnectorDto> Connectors { get; } = [];

    internal IntrospectionBuilder(PipelineOptions pipeline) => _pipeline = pipeline;

    // 段（コンシューマ）を自己申告に加える。段名・consumer 完全名・購読イベント型は型から、
    // 有効状態・出力は宣言から解決する。宣言が無い（Steps 空）場合は既定登録＝有効として申告する
    // （AddPlatformPipelineStep の登録規則 1 と整合）。
    public IntrospectionBuilder AddStep<TConsumer>()
        where TConsumer : class, IConsumer, IPipelineStep
    {
        var name = TConsumer.StepName;
        var consumer = typeof(TConsumer).FullName!;
        var input = typeof(TConsumer).GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
            ?.GetGenericArguments()[0].Name ?? string.Empty;

        var decl = _pipeline.FindStep(name);
        var enabled = decl?.Enabled ?? true;
        var outputs = decl?.Outputs is { Count: > 0 } o ? o : [];
        Steps.Add(new StepIntrospectionDto(name, consumer, input, outputs, enabled));
        return this;
    }

    // ADR-0027 / #441 E1: Wolverine 段の自己申告。**MassTransit の IConsumer を要求しない。**
    //
    // 🔴 **名前を AddStep にできない。** 制約はシグネチャの一部ではないため、制約違いの
    // オーバーロードは CS0111（重複メンバー）でコンパイルできない。引数で回避しても、
    // PartialMigrationSafetyValveTests の GetMethod("AddStep", …) が AmbiguousMatchException を
    // 投げ、**アサーションへ到達する前にテストが死ぬ**（同経路の唯一の防壁である）。
    // したがって別名にする。既存の AddStep は 1 バイトも変えない。
    //
    // 🔴 **入力型は IPipelineStep<TIn> から取り、導出できなければ起動失敗にする。**
    // MassTransit 版は IConsumer<> から導出し、導出できないと `?? string.Empty` で
    // **空文字を自己申告する**。それをドリフト検出が実行時の警告として拾う形になり、
    // 起動時には何も起きない。Wolverine 段では起動時に落とす（IADR-0239 決定 2 と同じ方針）。
    public IntrospectionBuilder AddWolverineStep<TStep>()
        where TStep : class, IPipelineStep
    {
        var name = TStep.StepName;
        var handler = typeof(TStep).FullName!;
        var inputType = typeof(TStep).GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineStep<>))
            ?.GetGenericArguments()[0]
            ?? throw new InvalidOperationException(
                $"段 '{name}' の実装 '{handler}' が IPipelineStep<TIn> を実装していないため"
                + " 入力イベント型を導出できません。空文字で自己申告するとドリフト検出が"
                + "実行時まで気づけないので、起動を止めます。");

        var decl = _pipeline.FindStep(name);
        var enabled = decl?.Enabled ?? true;
        var outputs = decl?.Outputs is { Count: > 0 } o ? o : [];
        Steps.Add(new StepIntrospectionDto(name, handler, inputType.Name, outputs, enabled));
        return this;
    }

    // 選択中のポート実装（例: vector-store / QdrantIngestionVectorStore / qdrant:6334）を申告する。
    public IntrospectionBuilder AddPort(string port, string implementation, string? target = null)
    {
        Ports.Add(new PortSelectionDto(port, implementation, target));
        return this;
    }

    // 登録済みコネクタと有効・無効状態を申告する。
    public IntrospectionBuilder AddConnector(string name, bool enabled)
    {
        Connectors.Add(new ConnectorDto(name, enabled));
        return this;
    }
}
