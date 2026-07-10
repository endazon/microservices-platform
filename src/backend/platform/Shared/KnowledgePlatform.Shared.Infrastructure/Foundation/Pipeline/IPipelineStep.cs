namespace KnowledgePlatform.Shared.Infrastructure.Foundation.Pipeline;

// FR-14, ADR-0018: パイプライン段の共通ステップインタフェース。
// 段（MassTransit コンシューマ）は本インタフェースを実装し、宣言的構成（pipeline.json の steps[].name）
// との対応をコンパイル時に固定する。購読は IConsumer<TIn>、発行は IPublishEndpoint が担い、
// 計画（10_composability-design.md §2）の Subscribe / Process / Publish 概念に対応する。
public interface IPipelineStep
{
    // 構成定義上の段名（例: "convert"）。pipeline.json の steps[].name と一致させる。
    static abstract string StepName { get; }
}
