using System.Globalization;
using Anthropic;
using Anthropic.Core;

namespace LlmGateway.Infrastructure.ExternalServices;

// FR-11, ADR-0010, ADR-0038 決定 4, IADR-0528, IADR-0562 (#1749): 公式 Claude SDK（NuGet `Anthropic`）の
// AnthropicClient の唯一の生成点。旧 `Anthropic.SDK` 4.0.0 向けの AnthropicHttpClient（期限つき HttpClient と委譲ハンドラ鎖）を置き換えた。
//
// 🔴 SDK の既定をそのまま使わない値が 4 つある（いずれも試験 T-34 / T-43 が固定する。IADR-0562 決定 2〜4）。
//   - MaxRetries = 0: SDK 既定は 2 回で 429・5xx・接続失敗・期限切れも再試行する。旧 SDK は再試行しなかった（実測）。
//     既定のままだと「429 は呼び出し側が再試行する」（ADR-0038 決定 4）と二重になり、フォールバック鎖と掛け算になり、
//     1 呼び出しの最長が期限 × 3 ＋待機になって「呼び出し側の期限 < この期限」（IADR-0528 決定 4）が崩れる。
//   - Timeout = `Llm:AnthropicTimeoutSeconds`（既定 100 秒）: SDK 既定は null で、要求ごとに max_tokens から算出する
//     （非ストリーム 30 秒〜10 分・ストリーム 10 分〜1 時間。非ストリームで 10 分を超える算出は例外）。明示すればこの値だけが使われる。
//     効く範囲は 1 試行の応答ヘッダの到着まで（SDK は ResponseHeadersRead で送る）。超過は TaskCanceledException（内側 TimeoutException）。
//     HttpClient.Timeout は InfiniteTimeSpan にし、期限の正をこの 1 か所にする（SDK の注記: 両方あると競合する）。
//   - BaseUrl は本番 URL を明示し、AuthToken は null を明示する: 明示しないと SDK は ANTHROPIC_BASE_URL / ANTHROPIC_AUTH_TOKEN を読む。
//     送信先と資格情報が設定の外（環境変数）で変わらないようにする（送信先の統制は FR-11 の本体）。
public static class AnthropicClientFactory
{
    public const string ApiKeyConfigKey = "Llm:ApiKey";
    public const string ConfigKey = "Llm:AnthropicTimeoutSeconds";
    public const int DefaultTimeoutSeconds = 100;

    // IADR-0562 決定 3: 再試行しない（旧 SDK の実測と同じ 1 回送信）。
    public const int MaxRetries = 0;

    // 期限の上限。SDK は 1 試行ごとに CancellationTokenSource(timeout) を作るので、int.MaxValue ミリ秒を超える値は扱えない
    // （IADR-0528 決定 2 の上限をそのまま引き継ぐ）。超える秒数は「不正」として既定へ倒す。
    public const int MaxTimeoutSeconds = int.MaxValue / 1000;

    // IADR-0528 決定 1・2: 期限の解決規則（未設定・空白は黙って既定、不正値は既定へ倒して warn 1 行）。値の規則は変えていない。
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

    // FR-11, #1997, IADR-0566: 逐次（CompleteStream）の無通信の期限。上流から次のイベントが届かないまま、この時間が過ぎたら打ち切る。
    // 未設定・空白は `Llm:AnthropicTimeoutSeconds` の解決値に従う（一括は生成の全体をこの値の内に終える。同じ用途・同じモデルの
    // 逐次で、イベントの間の無通信が一括の全体の期限より長いのは正常な生成ではない）。不正値は同じ解決値へ倒して warn 1 行。
    public const string StreamIdleConfigKey = "Llm:AnthropicStreamIdleTimeoutSeconds";

    public static TimeSpan ResolveStreamIdleTimeout(IConfiguration configuration, ILogger? logger = null)
    {
        var fallback = ResolveTimeout(configuration);
        var raw = configuration[StreamIdleConfigKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds is >= 1 and <= MaxTimeoutSeconds)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        logger?.LogWarning(
            "{ConfigKey} の値 '{Value}' は不正なため {FallbackKey} と同じ {FallbackSeconds} 秒を使う（1〜{MaxSeconds} の整数で指定する）。",
            StreamIdleConfigKey, raw, ConfigKey, (int)fallback.TotalSeconds, MaxTimeoutSeconds);
        return fallback;
    }

    // 一次ハンドラは既定の HttpClientHandler（システムプロキシ設定は既定で引き継がれる）で、応答圧縮を明示的に有効にする
    // （IADR-0114 決定 2 の後半を引き継ぐ）。試験は primaryHandler で送信先だけを差し替える（期限・再試行・送信先の設定は本番と同じ）。
    public static AnthropicClient Create(
        IConfiguration configuration, ILogger? logger = null, HttpMessageHandler? primaryHandler = null) =>
        new(new ClientOptions
        {
            ApiKey = configuration[ApiKeyConfigKey] ?? "placeholder",
            AuthToken = null,
            BaseUrl = EnvironmentUrl.Production,
            MaxRetries = MaxRetries,
            Timeout = ResolveTimeout(configuration, logger),
            HttpClient = new HttpClient(primaryHandler ?? new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            })
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            },
        });
}
