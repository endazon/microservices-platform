using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Pipeline;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;

// FR-15, ADR-0018: 各サービス・段の自己申告（イントロスペクション）を組み立て、
// メッシュ内部限定のエンドポイントとして公開する。構成情報 API（BFF）はこれを集約して
// 実効構成を組み立て、宣言（pipeline.json）と突合してドリフトを検出する。
public static class IntrospectionExtensions
{
    // 自己申告エンドポイントの内部パス（ingress へは公開しない。メッシュ内部限定）。
    public const string IntrospectionPath = "/internal/introspection";

    // サービスの自己申告（購読/発行する段・選択中ポート・コネクタ）を構築して登録する。
    // 段の実効値（enabled・outputs）は宣言（pipeline）から解決する（登録規則と同じ導出）。
    public static IServiceCollection AddKnowledgePlatformIntrospection(
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
    public static IEndpointRouteBuilder MapKnowledgePlatformIntrospection(this IEndpointRouteBuilder app)
    {
        app.MapGet(IntrospectionPath,
            (HttpContext ctx) => Results.Ok(
                ctx.RequestServices.GetRequiredService<ServiceIntrospectionDto>()))
           .WithName("KnowledgePlatformIntrospection")
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
    // （AddKnowledgePlatformPipelineStep の登録規則 1 と整合）。
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
