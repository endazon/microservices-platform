using System.Diagnostics.Metrics;

namespace LlmGateway.Common.Observability;

// FR-11, NFR-19, ADR-0006, IADR-0114, IADR-0535 (#1917): Anthropic 応答から除去した content ブロックの件数。
//
// 5.5 系は 3 モデルとも thinking を無効にできず（IADR-0531）、thinking の除去は通常の応答の形である。
// 1 応答 1 行の Warning では本物の警告が埋もれる（PoC の 7 時間で 685 行）ため、ログは Debug へ下げ、
// **件数はこの計器で数える**（傾向は計器、個別はログ。IADR-0110 と同じ分担）。
//
// 属性 llm.content_block_type は 3 値に閉じる。型名は上流（API）が決める非有界な値なので、
// 想定内の 2 型以外は other へ集約する（原文は Warning のログ側に載る）。
public sealed class AnthropicContentBlockMetrics
{
    // Meter は補完カウンタと同じもの（Program.cs の AddMeter は 1 つで足りる）。
    public const string MeterName = LlmCompletionMetrics.MeterName;
    public const string DroppedCounterName = "llm.anthropic.content_blocks_dropped";
    public const string BlockTypeTag = "llm.content_block_type";

    public const string TypeThinking = "thinking";
    public const string TypeRedactedThinking = "redacted_thinking";
    public const string TypeOther = LlmMetricValues.Other;

    // 除去が想定内の型（5.5 系の拡張思考が常に返す）。これ以外の除去は想定外として Warning にする。
    public static readonly IReadOnlySet<string> ExpectedDroppedTypes =
        new HashSet<string>(StringComparer.Ordinal) { TypeThinking, TypeRedactedThinking };

    private readonly Counter<long> _dropped;

    public AnthropicContentBlockMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _dropped = meter.CreateCounter<long>(
            DroppedCounterName, unit: "{block}",
            description: "Anthropic 応答から SDK 解釈前に除去した content ブロックの件数（型別。想定内は thinking / "
                       + "redacted_thinking、それ以外は other）。other の増加は応答形状の変化を表す。");
    }

    // 除去した型名を 1 件ずつ数える。
    public void RecordDropped(IEnumerable<string> droppedTypes)
    {
        foreach (var type in droppedTypes)
            _dropped.Add(1, new KeyValuePair<string, object?>(BlockTypeTag, Normalize(type)));
    }

    public static string Normalize(string type) =>
        ExpectedDroppedTypes.Contains(type) ? type : TypeOther;
}
