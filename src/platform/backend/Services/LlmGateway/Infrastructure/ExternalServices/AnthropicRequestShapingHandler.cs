using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmGateway.Infrastructure.ExternalServices;

// FR-11, ADR-0010, IADR-0531 (#1875・planning#783): Anthropic Messages API への**要求本文**へ
// `output_config.effort` を足す委譲ハンドラ（応答側の AnthropicResponseSanitizingHandler と対になる要求側）。
//
// ここ（HttpClient の層）で行う理由: Anthropic.SDK 4.0.0 は effort を送る口を持たない（#1749 の SDK 移行の判断とは独立に、
// 用途別 effort だけを先に持つ）。値は呼び出し単位の文脈（AnthropicRequestContext）から読む。
//
// 触らない条件（いずれも本文を 1 バイトも変えない）:
//   - 文脈に effort が無い（既定。設定 `Llm:PurposeEffort` に無い用途・effort 非対応のモデル）。
//   - POST でない・本文が無い・本文が JSON オブジェクトでない。
//   - 本文に既に `output_config.effort` がある（呼び出し側の明示を上書きしない）。
// 🔴 temperature / top_p / top_k / thinking / tool_choice は**足さない**（5.5 系で 400 になる。IADR-0531 決定 4）。
public sealed class AnthropicRequestShapingHandler : DelegatingHandler
{
    private const string JsonMediaType = "application/json";

    // 再直列化で日本語のプロンプトを \uXXXX へ膨らませない（JSON としては同値。送信量だけが変わる）。
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var effort = AnthropicRequestContext.Effort;
        if (effort is not null && request.Method == HttpMethod.Post && request.Content is { } content)
        {
            var body = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (TryAddEffort(body, effort, out var shaped))
            {
                var replacement = new StringContent(shaped, Encoding.UTF8, JsonMediaType);
                foreach (var header in content.Headers)
                {
                    if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                request.Content = replacement;
                content.Dispose();
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // 本文の JSON オブジェクトへ output_config.effort を足す。足さなかったら false。
    public static bool TryAddEffort(string body, string effort, out string shaped)
    {
        shaped = body;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root is not JsonObject obj)
            return false;

        if (obj["output_config"] is JsonObject existing)
        {
            if (existing.ContainsKey("effort"))
                return false;
            existing["effort"] = effort;
        }
        else if (obj.ContainsKey("output_config"))
        {
            // 形の分からない output_config（null・非オブジェクト）は触らない。
            return false;
        }
        else
        {
            obj["output_config"] = new JsonObject { ["effort"] = effort };
        }

        shaped = obj.ToJsonString(WriteOptions);
        return true;
    }
}
