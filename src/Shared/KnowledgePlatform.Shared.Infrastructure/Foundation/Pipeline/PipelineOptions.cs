namespace KnowledgePlatform.Shared.Infrastructure.Foundation.Pipeline;

// FR-14, ADR-0018: 宣言的パイプライン構成（構成セクション `Pipeline`）のバインドモデル。
// 正となる宣言は deploy/helm/knowledge-platform/files/pipeline.json（Git 管理・CI スキーマ検証）。
public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";

    public int Version { get; set; } = 1;

    // 段（イベント購読→処理→発行）の宣言。空＝宣言なし（既定配線で動作。ローカル・テスト互換）。
    public List<PipelineStepOptions> Steps { get; set; } = [];

    public PipelineStepOptions? FindStep(string name) =>
        Steps.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
}

public sealed class PipelineStepOptions
{
    // 段名（pipeline.json steps[].name。IPipelineStep.StepName と一致）
    public string Name { get; set; } = "";

    // 段をホストするサービス名（例: conversion-service）
    public string Service { get; set; } = "";

    // 段を実装するコンシューマの .NET 型完全名（誤構成の fail-fast 照合に用いる）
    public string Consumer { get; set; } = "";

    // 入力イベント型名（IConsumer<TIn> の TIn と一致）
    public string Input { get; set; } = "";

    // 出力イベント型名（CI の接続性・循環検証に用いる。実行時検証は #112 ドリフト検出）
    public List<string> Outputs { get; set; } = [];

    // 受信キュー名の上書き（省略時は MassTransit 既定の命名）
    public string? Queue { get; set; }

    // 段の有効/無効（false で購読・キューを生成しない）
    public bool Enabled { get; set; } = true;
}
