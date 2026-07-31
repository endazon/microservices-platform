using System.Text.Encodings.Web;
using System.Text.Json;

namespace LlmGateway.Api.Composable.Adapters;

// FR-04, ADR-0010, IADR-0114 (#290): Anthropic Messages API 応答の content ブロックのうち、
// Anthropic.SDK 4.0.0 が解釈できない型を取り除く純関数。
//
// 課題: SDK の content 判別子は text / image / tool_use / tool_result の 4 種しか知らず、
// それ以外（thinking / redacted_thinking / server_tool_use / 将来追加される型）が 1 個でも
// 含まれると `JsonException: Unknown type <型>` で**応答全体**の解析に失敗する。
// 用途別の割当モデル（Opus 5 / Sonnet 5 / Fable 5）はいずれも thinking が既定で有効なので、
// 素の SDK では非ストリーミング /complete が全件失敗する。
//
// 方針: 型名の**許可リスト**で残す（拒否リストではない）。将来 API が新しいブロック型を
// 追加しても、こちら側の更新なしに自動的に落ちる＝未知型で応答全体を失わない。
// 本文（text）とツール結果は従来どおり残る。
public static class AnthropicContentBlockSanitizer
{
    // SDK 4.0.0 の ContentConverter が解釈できる型。実測で確定させている
    // （AnthropicContentBlockSanitizerTests / ClaudeProviderThinkingTests）。
    private static readonly HashSet<string> KnownContentTypes =
        new(StringComparer.Ordinal) { "text", "image", "tool_use", "tool_result" };

    // 日本語を \uXXXX へ過剰エスケープしない（本文の可読性と帯域。CompletionEndpoints と同じ方針）。
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 未知の content ブロックを取り除いた JSON を返す。書き換えが不要／不能な場合は false を返し、
    /// 呼び出し側は元の本文をそのまま使う（既知型だけの応答は 1 バイトも触らない）。
    /// </summary>
    public static bool TrySanitize(string json, out string sanitized, out IReadOnlyList<string> droppedTypes)
    {
        sanitized = json;
        droppedTypes = [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // 解釈できない本文は触らない（壊れた応答をさらに壊さない）。
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                // メッセージ応答ではない（エラー応答など）。対象外。
                return false;
            }

            var dropped = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (!IsKnown(block))
                    dropped.Add(TypeNameOf(block));
            }

            if (dropped.Count == 0)
                return false;

            droppedTypes = dropped;
            sanitized = Rewrite(root, content);
            return true;
        }
    }

    private static bool IsKnown(JsonElement block) =>
        block.ValueKind == JsonValueKind.Object
        && block.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && KnownContentTypes.Contains(type.GetString()!);

    // 型名が取れない要素も未知として落とすため、記録用の表示名を用意する。
    private static string TypeNameOf(JsonElement block) =>
        block.ValueKind == JsonValueKind.Object
        && block.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
            ? type.GetString()!
            : "(型不明)";

    private static string Rewrite(JsonElement root, JsonElement content)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("content"))
                {
                    writer.WritePropertyName(property.Name);
                    writer.WriteStartArray();
                    foreach (var block in content.EnumerateArray())
                    {
                        if (IsKnown(block))
                            block.WriteTo(writer);
                    }

                    writer.WriteEndArray();
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
