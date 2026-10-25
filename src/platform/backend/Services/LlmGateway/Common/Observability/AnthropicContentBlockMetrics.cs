using System.Diagnostics.Metrics;

namespace LlmGateway.Common.Observability;

// FR-11, NFR-19, ADR-0006, IADR-0114, IADR-0535 (#1917), IADR-0562 (#1749): Anthropic 応答の content のうち、
// 本文（text ブロック）に使わず捨てたブロックの件数。
// ［2026-10-17 / #1749］旧 SDK では「SDK の解釈前に委譲ハンドラが除去した件数」だった。公式 SDK は未知の型も保持するので、
// 数える場所は ClaudeProvider（本文を取り出すところ）へ移した。計器の名前・属性・値域は変えていない（ダッシュボードの継続のため）。
//
// 5.5 系は 3 モデルとも thinking を無効にできず（IADR-0531）、thinking の除去は通常の応答の形である。
// 1 応答 1 行の Warning では本物の警告が埋もれる（PoC の 7 時間で 685 行）ため、ログは Debug へ下げ、
// **件数はこの計器で数える**（傾向は計器、個別はログ。IADR-0110 と同じ分担）。
//
// 属性 llm.content_block_type は 4 値に閉じる。型名は上流（API）が決める非有界な値なので、
// 想定内の 2 型と text 以外は other へ集約する（原文は Warning のログ側に載る）。
// ［2026-10-20 / #2000・IADR-0564］値 text を足した。本文には先頭の text ブロックだけを使うので、2 個目以降の text ブロックは
// 本文に使わず捨てている。それを数える（従前は数えずに捨てていた）。text は常に想定外（Warning）である。
public sealed class AnthropicContentBlockMetrics
{
    // Meter は補完カウンタと同じもの（Program.cs の AddMeter は 1 つで足りる）。
    public const string MeterName = LlmCompletionMetrics.MeterName;
    public const string DroppedCounterName = "llm.anthropic.content_blocks_dropped";
    public const string BlockTypeTag = "llm.content_block_type";

    public const string TypeThinking = "thinking";
    public const string TypeRedactedThinking = "redacted_thinking";
    public const string TypeOther = LlmMetricValues.Other;

    // 2 個目以降の text ブロック（本文に使わなかった本文）。ExpectedDroppedTypes には入れない（想定外）。
    public const string TypeText = "text";

    // 捨てるのが想定内の型（5.5 系の拡張思考が常に返す）。これ以外は想定外として Warning にする。
    public static readonly IReadOnlySet<string> ExpectedDroppedTypes =
        new HashSet<string>(StringComparer.Ordinal) { TypeThinking, TypeRedactedThinking };

    private readonly Counter<long> _dropped;

    public AnthropicContentBlockMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _dropped = meter.CreateCounter<long>(
            DroppedCounterName, unit: "{block}",
            description: "Anthropic 応答の content のうち本文に使わず捨てたブロックの件数（型別。想定内は thinking / "
                       + "redacted_thinking、2 個目以降の text ブロックは text、それ以外は other）。"
                       + "other・text の増加は応答形状の変化を表す。");
    }

    // 捨てた型名を 1 件ずつ数える。
    public void RecordDropped(IEnumerable<string> droppedTypes)
    {
        foreach (var type in droppedTypes)
            _dropped.Add(1, new KeyValuePair<string, object?>(BlockTypeTag, Normalize(type)));
    }

    public static string Normalize(string type) =>
        ExpectedDroppedTypes.Contains(type) || type == TypeText ? type : TypeOther;
}
