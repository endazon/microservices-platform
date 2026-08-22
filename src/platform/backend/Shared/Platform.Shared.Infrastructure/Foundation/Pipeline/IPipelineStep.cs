namespace Platform.Shared.Infrastructure.Foundation.Pipeline;

// FR-14, ADR-0018: パイプライン段の共通ステップインタフェース。
// 段（MassTransit コンシューマ）は本インタフェースを実装し、宣言的構成（pipeline.json の steps[].name）
// との対応をコンパイル時に固定する。購読は IConsumer<TIn>、発行は IPublishEndpoint が担い、
// 計画（10_composability-design.md §2）の Subscribe / Process / Publish 概念に対応する。
public interface IPipelineStep
{
    // 構成定義上の段名（例: "convert"）。pipeline.json の steps[].name と一致させる。
    static abstract string StepName { get; }
}

// FR-14, ADR-0018 / ADR-0027: 段が受け取る入力イベント型を**型として**申告するインタフェース。
//
// 🔴 **Wolverine 経路で pipeline.json の `input` 宣言と突き合わせるための唯一の情報源である。**
// MassTransit 経路は `IConsumer<TIn>` から導出できたが、Wolverine 段にはそれが無い。
// 導出元が無いまま照合しようとすると **照合が黙って素通りする**（IADR-0234 決定 4 が
// `IntrospectionBuilder.AddStep` について記録した「制約だけ外すと input が string.Empty になる」
// のと同じ形）。本インタフェースはその導出元を型で与える。
//
// 既存の MassTransit 段は実装しない（素の IPipelineStep のまま）。**新規の Wolverine 段が実装する。**
public interface IPipelineStep<TIn> : IPipelineStep
    where TIn : class;
