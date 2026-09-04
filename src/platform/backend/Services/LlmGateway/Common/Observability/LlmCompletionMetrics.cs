using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Common.Observability;

// FR-11, NFR, ADR-0006, IADR-0110 (#395): 補完（completion）の終了理由を計上するカウンタ。
// IADR-0104 で終了理由はログに残るようになったが、拒否がどの頻度で起きているかを継続的に把握する
// 手段が無かった。拒否率は既定モデルの妥当性・特定用途の恒常的拒否・障害と拒否の切り分けに直結する。
//
// 設計の要点は「属性値の値域を閉じること」である。時系列の系列数は属性値の直積で増えるため、
// 非有界な値を 1 つ混ぜるとカーディナリティが爆発する。本経路では purpose（呼び出し側の自由文字列）と
// stop_reason（未知値を原文透過する。IADR-0104 / IADR-0109）が非有界になり得るため、いずれも
// 既知集合以外は other へ集約する。原文が必要な調査はログ側で行う（メトリクスは傾向、ログは個別）。
// プロンプト・本文・利用者識別子は属性にしない。
//
// IADR-0374 (#1091): 上流 HTTP ステータスの軸（llm.upstream_status）も同じ規律で足した。
// 429 が 5xx・通信断と一緒に upstream_error の一点へ潰れており、レート制限の有無を
// メトリクスだけでは判定できなかった。**生のステータスは載せず 6 値へ正規化する。**
public sealed class LlmCompletionMetrics
{
    // Meter 名はサービス名と一致させる（Program.cs の AddMeter と OTLP の収集対象）。
    public const string MeterName = "microservices-platform.llm-gateway";
    public const string CompletionCounterName = "llm.completion.total";
    public const string OutputTokensHistogramName = "llm.completion.output_tokens";

    public const string ResultTag = "llm.result";
    public const string StopReasonTag = "llm.stop_reason";
    public const string PurposeTag = "llm.purpose";
    public const string ModelTag = "llm.model";
    public const string ProviderTag = "llm.provider";
    public const string ConfidentialityTag = "llm.confidentiality";
    // IADR-0374 (#1091): 上流が返したものの軸。llm.result（**基盤側が何をしたか**）とは独立している。
    public const string UpstreamStatusTag = "llm.upstream_status";

    // llm.result: 送信可否の軸（FR-11 の Sent に対応）。stop_reason とは独立した軸である（IADR-0104）。
    public const string ResultSent = "sent";                     // 越境が成立した（拒否率の分母）
    public const string ResultEgressDenied = "egress_denied";    // 機密区分により送信しなかった
    public const string ResultProviderMissing = "provider_missing"; // 呼び出し先プロバイダ未登録
    public const string ResultUpstreamError = "upstream_error";  // 呼び出し先が不調（例外）
    // ADR-0038 決定 6 (#863), IADR-0225: 上流が HTTP 400 系を返し、**次の候補モデルへ切り替えた**呼び出し。
    // フォールバックが起きた 1 リクエストは 2 回計上される（見送った第 1 候補が fallback、
    // 成功した第 2 候補が sent）。llm.model が候補ごとに違うため、用途別・モデル別の利用実績として読める。
    // ★ upstream_error に混ぜない —— 混ぜると「回復した呼び出し」が呼び出し先障害の率へ入り、
    //   upstream_error 率 > 10%（critical）のアラート方針が誤発火する。
    public const string ResultFallback = "fallback";

    // 未知値の集約先と「該当なし」。
    public const string ValueOther = "other";
    public const string ValueNone = "none";

    // IADR-0374 (#1091), ADR-0038 決定 4: llm.upstream_status の値域（**6 値に閉じる**）。
    // 429 は LlmFallbackPolicy が「フォールバックさせない失敗」として正しく分類していたが、
    // 分類結果がメトリクスに残らず、5xx・通信断・その他と一緒に upstream_error の一点へ潰れていた。
    //
    // ★ **生の HTTP ステータスは載せない。** 上流の仕様変更で値域が増える非有界な軸であり、
    //   本クラスの設計原則（属性値の値域を閉じる）に反する。原文が要る調査はログ側で行う
    //   （非フォールバック側の LogError にも {Status} を構造化フィールドで出している）。
    public const string UpstreamRateLimited = "rate_limited";  // 429。再試行の対象（ADR-0038 決定 4）
    public const string UpstreamClientError = "client_error";  // 400–499（429 を除く）
    public const string UpstreamServerError = "server_error";  // 500–599
    // ステータスが取れず、ネットワーク層の失敗の形をしているもの（接続不能・名前解決失敗・タイムアウト）。
    // ★ 設定ミス（BaseUrl 未設定の InvalidOperationException 等）をここへ混ぜない ——
    //   混ぜると「呼び出し先の通信障害」と「自分の設定の誤り」が同じ系列になり、直す対象を取り違える。
    //   そちらは ValueOther へ落ちる。
    public const string UpstreamTransport = "transport";

    // IADR-0374 (#1091): llm.upstream_status が取り得る値の**全体**。テストはこの集合で値域を固定する
    // （「429 という生の値が出ない」ことを、実装の分岐ではなくこの宣言に対して確かめる）。
    public static readonly IReadOnlySet<string> UpstreamStatusValues =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ValueNone, UpstreamRateLimited, UpstreamClientError, UpstreamServerError,
            UpstreamTransport, ValueOther,
        };

    // メトリクス属性として許す終了理由（正準語彙。IADR-0104 / IADR-0109）。
    private static readonly HashSet<string> KnownStopReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        CompletionStopReasons.EndTurn,
        CompletionStopReasons.MaxTokens,
        CompletionStopReasons.Refusal,
        CompletionStopReasons.StopSequence,
        CompletionStopReasons.ToolUse,
    };

    // IADR-0212 (#786): 既定の max_tokens 4096（IADR-0101）の妥当性を読むための境界。
    // 上限付近を細かく刻む（2048 / 3072 / 4096）—— 「上限に張り付いているのか、余裕があるのか」が
    // 4096 の再調整の判断材料そのものだからである。4096 超は既定を上げた場合の観測用に 1 段だけ置く。
    private static readonly int[] OutputTokenBuckets =
        [0, 16, 64, 128, 256, 512, 1024, 2048, 3072, 4096, 8192];

    private readonly Counter<long> _completions;
    private readonly Histogram<int> _outputTokens;
    private readonly IOptionsMonitor<LlmRoutingOptions> _routing;

    public LlmCompletionMetrics(IMeterFactory meterFactory, IOptionsMonitor<LlmRoutingOptions> routing)
    {
        _routing = routing;
        var meter = meterFactory.Create(MeterName);
        _completions = meter.CreateCounter<long>(
            CompletionCounterName, unit: "{completion}",
            description: "LLM 補完の呼び出し結果（送信可否 × 終了理由 × 上流ステータス）。拒否率は "
                       + "stop_reason=refusal ÷ result=sent、レート制限は upstream_status=rate_limited で読む。");
        // 送信が成立した呼び出しだけを分布として持つ（未送信にはトークン数が存在しない。IADR-0212 決定 3）。
        _outputTokens = meter.CreateHistogram<int>(
            OutputTokensHistogramName, unit: "{token}",
            description: "送信が成立した LLM 補完の出力トークン数の分布。max_tokens の妥当性は "
                       + "上限付近のバケットの厚みで読む（IADR-0101 / #380）。",
            tags: null,
            advice: new InstrumentAdvice<int> { HistogramBucketBoundaries = OutputTokenBuckets });
    }

    // 補完 1 回を計上する。**送信していない経路も計上する**（分母が欠けると拒否率が過大に見える）。
    //   result       — 送信可否の軸（ResultSent / ResultEgressDenied / ResultProviderMissing / ResultUpstreamError）
    //   stopReason   — モデル側の終了理由。未送信・未報告は null（属性は none になる）
    //   outputTokens — 出力トークン数。**送信が成立した経路だけが値を持つ**（IADR-0212 決定 3）。
    //                  null の経路では Histogram を記録しない —— 0 を積むと分布の最下段が
    //                  「短い応答」と「応答が無かった」の混合になり、上限到達の判断が濁る。
    //   failure      — 上流の失敗を表す例外。**文字列ではなく例外を受ける**（IADR-0374 決定 4）——
    //                  文字列を受ける口にすると呼び出し側が ex.Message（プロンプト断片・利用者識別子を
    //                  含み得る）を渡せてしまう。タグ値は下の分類器の戻り値以外にはなり得ない。
    public void RecordCompletion(
        string result, string? stopReason, RoutingDecision decision, string purpose,
        SensitivityClass sensitivity, int? outputTokens = null, Exception? failure = null)
    {
        var tags = new TagList
        {
            { ResultTag, result },
            { StopReasonTag, NormalizeStopReason(stopReason) },
            { PurposeTag, NormalizePurpose(purpose) },
            { ModelTag, Or(decision.Model, ValueNone) },
            { ProviderTag, Or(decision.Provider, ValueNone) },
            { ConfidentialityTag, sensitivity.ToString().ToLowerInvariant() },
            { UpstreamStatusTag, ClassifyUpstreamStatus(failure) },
        };
        _completions.Add(1, tags);

        if (outputTokens is not { } tokens)
            return;
        // llm.result は Histogram では常に sent になり系列を分けないため落とす（IADR-0212 決定 2）。
        // llm.upstream_status も同じ理由で落とす —— Histogram は送信が成立した経路にしか記録せず、
        // その経路の値は常に none である（IADR-0374 決定 5）。
        var histogramTags = new TagList();
        foreach (var tag in tags)
        {
            if (tag.Key != ResultTag && tag.Key != UpstreamStatusTag)
                histogramTags.Add(tag);
        }
        _outputTokens.Record(tokens, histogramTags);
    }

    // IADR-0374 (#1091): 上流の失敗を**値域を閉じた 6 値**へ写す。
    //
    // ステータスの取り出しは LlmFallbackPolicy.StatusCodeOf（例外連鎖から HTTP ステータスを取る
    // 唯一の実装）へ委ねる。**抽出を二重に書かない** —— フォールバック判定（IADR-0225）と
    // 同じ一次情報から導くことで、片方だけが上流 SDK の変化に取り残される事故を避ける。
    // **判定そのもの（ShouldFallBack）は複製しない。** ここが決めるのは「上流が何を返したか」だけで、
    // 「それに対して何をするか」は引き続き LlmFallbackPolicy が単独で決める。
    public static string ClassifyUpstreamStatus(Exception? failure)
    {
        if (failure is null)
            return ValueNone;

        if (LlmFallbackPolicy.StatusCodeOf(failure) is { } status)
            return status switch
            {
                LlmFallbackPolicy.RateLimitStatusCode => UpstreamRateLimited,
                >= 400 and < 500 => UpstreamClientError,
                >= 500 and < 600 => UpstreamServerError,
                // 1xx / 2xx / 3xx が例外として届くことは想定していないが、来たら other へ集約する
                // （値域を閉じる原則を、想定外の入力でも破らない）。
                _ => ValueOther,
            };

        return IsTransportFailure(failure) ? UpstreamTransport : ValueOther;
    }

    // ステータスが取れない失敗のうち、ネットワーク層の形をしているものだけを transport とする。
    // ラップされている場合に備え InnerException も辿る（StatusCodeOf と同じ走査方針）。
    private static bool IsTransportFailure(Exception failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or SocketException or TimeoutException or IOException)
                return true;
        }

        return false;
    }

    // 正準語彙のみ属性に載せ、未知値は other へ集約する。プロバイダ側の語彙追加が系列増加に直結しないようにする。
    private static string NormalizeStopReason(string? stopReason)
    {
        if (string.IsNullOrWhiteSpace(stopReason))
            return ValueNone;
        return KnownStopReasons.Contains(stopReason) ? stopReason.ToLowerInvariant() : ValueOther;
    }

    // purpose は呼び出し側が自由に指定できるため、設定（PurposeModels）で値域を閉じる。
    // other の増加は「定義していない purpose が来ている」＝ルーティングが既定へ落ちている状態の遅い警報にもなる。
    //
    // ［#443］正規化の実体は LlmMetricValues へ出した。**費用系の計器（LlmUsageMetrics）と同じ軸で
    // 読めなければ用途別モデル振り分けの効果を測れない**ため（ADR-0044 決定 1）、値域の定義を 1 箇所に持つ。
    //
    // PurposeModels は LlmRoutingOptions 側で StringComparer.OrdinalIgnoreCase の辞書として初期化されており
    // （設定バインダはその辞書インスタンスへマージする）、キー照合は大小文字非依存になる。
    private string NormalizePurpose(string purpose)
        => LlmMetricValues.NormalizePurpose(_routing.CurrentValue, purpose);

    private static string Or(string? value, string fallback)
        => LlmMetricValues.Or(value, fallback);
}
