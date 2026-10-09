using System.Globalization;

namespace LlmGateway.Infrastructure.ExternalServices;

// FR-11, ADR-0010, IADR-0114, IADR-0528 (#1872): AnthropicClient へ渡す HttpClient の生成点。
//
// 期限（HttpClient.Timeout）は `Llm:AnthropicTimeoutSeconds` から読む。**既定は 100 秒**（.NET の既定と同値。
// 従前は Timeout を設定せず暗黙に 100 秒だった。利用者裁定 2026-10-10 で既定は据え置き）。
// 非ストリーミングの /complete は全文の生成を待つため、長い出力（max_tokens 8192 等）はこの期限で頭打ちになる。
// ストリーミングでは最初の応答ヘッダが届くまでにしか効かない（SDK はヘッダ到着で SendAsync を完了し、SSE は素通し）。
// 延ばすのは運用者の判断であり、**呼び出し側の期限はこの値より短く保つ**（docs/operations/operations.md）。
//
// 不正な値（数値でない・0 以下・HttpClient が受け付けない大きさ）は既定へ倒し、起動は止めない。
// 値が在って採らなかったときだけ warn を 1 行出す（未設定・空白は黙って既定）。
public static class AnthropicHttpClient
{
    public const string ConfigKey = "Llm:AnthropicTimeoutSeconds";
    public const int DefaultTimeoutSeconds = 100;

    // HttpClient.Timeout の setter は int.MaxValue ミリ秒を超える値で ArgumentOutOfRangeException を投げる。
    // それを超える秒数は「不正」として既定へ倒す（AnthropicClient の初回解決＝最初の Claude 呼び出しが落ちるのを避ける）。
    public const int MaxTimeoutSeconds = int.MaxValue / 1000;

    public static TimeSpan ResolveTimeout(IConfiguration configuration, ILogger? logger = null)
    {
        var raw = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        }

        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds is >= 1 and <= MaxTimeoutSeconds)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        logger?.LogWarning(
            "{ConfigKey} の値 '{Value}' は不正なため既定の {DefaultSeconds} 秒を使う（1〜{MaxSeconds} の整数で指定する）。",
            ConfigKey, raw, DefaultTimeoutSeconds, MaxTimeoutSeconds);
        return TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    }

    // IADR-0114 (AST#290): 応答サニタイズの委譲ハンドラを噛ませる。一次ハンドラは既定の HttpClientHandler
    // （システムプロキシ設定は既定で引き継がれる）で、応答圧縮だけは明示的に有効化する。
    // IADR-0531 (#1875): 最も外側に要求本文の整形（用途別 effort の注入）を置く。
    // 鎖: AnthropicRequestShapingHandler → AnthropicResponseSanitizingHandler → HttpClientHandler。
    public static HttpClient Create(
        IConfiguration configuration,
        ILogger<AnthropicResponseSanitizingHandler> sanitizerLogger,
        ILogger? logger = null) =>
        new(new AnthropicRequestShapingHandler
        {
            InnerHandler = new AnthropicResponseSanitizingHandler(sanitizerLogger)
            {
                InnerHandler = new HttpClientHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                },
            },
        })
        {
            Timeout = ResolveTimeout(configuration, logger),
        };
}
