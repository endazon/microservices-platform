using System.Net.Http.Headers;
using System.Text;

namespace LlmGateway.Api.Composable.Adapters;

// FR-04, ADR-0010, IADR-0114 (#290): Anthropic Messages API の JSON 応答から、SDK が解釈できない
// content ブロック型を取り除いてから SDK のデシリアライズへ渡す委譲ハンドラ。
//
// ここ（HttpClient の層）で行う理由: Anthropic.SDK 4.0.0 は content の多態デシリアライズを
// 自前の JsonSerializerOptions で完結させており、外部から変換器を差し込む口が無い。
// 応答本文を通過時に整えるのが、SDK を差し替えずに未知型へ耐性を持たせる唯一の接合点。
//
// 対象を絞る根拠:
//   - 2xx かつ application/json のみ。非 2xx は SDK 側の例外整形へ委ねる。
//   - SSE（text/event-stream）は触らない。実測で SDK のストリーム経路は thinking ブロックと
//     thinking_delta / signature_delta を素通しでき（本文デルタも正常）、壊れていない。
//     ストリームを丸ごとバッファリングして遅延を持ち込まないためでもある。
public sealed class AnthropicResponseSanitizingHandler(ILogger<AnthropicResponseSanitizingHandler> logger)
    : DelegatingHandler
{
    private const string JsonMediaType = "application/json";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode
            || !string.Equals(response.Content.Headers.ContentType?.MediaType, JsonMediaType, StringComparison.OrdinalIgnoreCase))
        {
            return response;
        }

        // ReadAsStringAsync は本文を HttpContent 内部へバッファリングするため、書き換えない場合は
        // 応答をそのまま返せば後段（SDK）が同じ本文を読み直せる。
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!AnthropicContentBlockSanitizer.TrySanitize(body, out var sanitized, out var droppedTypes))
        {
            // 既知型だけの応答は 1 バイトも触らない。「サニタイズ自体が本文を変えてしまう」経路を
            // 既定で持たないため、再直列化も差し替えも行わない。
            return response;
        }

        // 落としたブロック型は必ず記録する。無言で捨てると、モデル側の応答形状が変わったことに
        // 気づけないまま本文が空になり得る（型名のみ・本文は載せない＝機微を残さない）。
        logger.LogWarning(
            "Anthropic 応答から SDK 未対応の content ブロックを除去しました（型: {DroppedTypes}）。本文と既知ブロックは保持しています。",
            string.Join(", ", droppedTypes));

        return ReplaceContent(response, sanitized);
    }

    // ReadAsStringAsync で消費済みのストリームを、後段（SDK）が再度読めるように詰め直す。
    private static HttpResponseMessage ReplaceContent(HttpResponseMessage response, string body)
    {
        var headers = response.Content.Headers;
        var replacement = new StringContent(body, Encoding.UTF8, JsonMediaType);
        CopyContentHeaders(headers, replacement.Headers);

        response.Content.Dispose();
        response.Content = replacement;
        return response;
    }

    // Content-Type / Content-Length は差し替え後の実体に合わせて設定済みのため引き継がない。
    private static void CopyContentHeaders(HttpContentHeaders source, HttpContentHeaders destination)
    {
        foreach (var header in source)
        {
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            destination.TryAddWithoutValidation(header.Key, header.Value);
        }
    }
}
