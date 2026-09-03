using System.Diagnostics.Metrics;

namespace AiAnalysisService.Common.Observability;

// NFR-02, NFR-21, FR-04, UC-01, SC-01, ADR-0006, ADR-0076 決定 5, IADR-0365 (#1204):
// RAG 回答の **初回トークンまでの時間（TTFT）** を測るヒストグラム。
//
// 計画の SLI は「RAG 回答 初回応答 p95 5 秒」である。これまで代理値として読んでいた
// `http_server_request_duration_seconds`（応答完了までの所要時間）は**別物**であり、
// ADR-0076 は SLI を応答完了 p95 へ改める案を却下した —— **長い回答ほど SLO 違反になり、
// 回答品質を上げると SLO が悪化する逆向きの誘因が生じる**ためである。
// SLI の定義は変えず、計器の側を SLI に合わせる（ADR-0076 決定 5）。
//
// 🔴 **単位は秒である**（ADR-0076 決定 1。OTel 安定版 HTTP セマンティック規約の
// `http.server.request.duration` に揃える）。ミリ秒の計器を選ぶと **1000 倍ずれた閾値が静かに成立する**
// —— #1110 がまさにそれであった（`http_server_duration_milliseconds_*` を見る 4 ルールが永久に発火しなかった）。
//
// 設計の要点は `LlmCompletionMetrics` と同じ「**属性値の値域を閉じること**」である。
// 時系列の系列数は属性値の直積で増えるため、非有界な値を 1 つ混ぜるとカーディナリティが爆発する。
// **プロンプト・質問文・検索語・利用者識別子は属性にしない。**
public sealed class RagStreamMetrics
{
    // Meter 名はサービス名と一致させる（Program.cs の ServiceName / AddMeter と OTLP の収集対象）。
    public const string MeterName = "microservices-platform.aianalysis-service";
    public const string FirstTokenHistogramName = "rag.answer.first_token.duration";

    // 用途の軸。`llm.completion.total` の `llm.purpose` と同じ値を採り、両者を同じ軸で読めるようにする。
    public const string PurposeTag = "ai.purpose";

    // RagOrchestrator が /analysis/ask/stream で LlmGateway へ渡している用途そのもの。
    public const string PurposeRagAnswer = "rag-answer";

    // 未知値の集約先（値域を閉じる。呼び出し側が自由文字列を渡しても系列は増えない）。
    public const string ValueOther = "other";

    private static readonly HashSet<string> KnownPurposes =
        new(StringComparer.OrdinalIgnoreCase) { PurposeRagAnswer };

    // 🔴 **境界に SLO のしきい値 5（秒）を必ず置く。** 境界に無いと histogram_quantile が
    // 隣の境界へ内挿し、しきい値の前後で判定が滑る。上側は「遅い」の程度が読めるだけの粗さで足りる。
    private static readonly double[] FirstTokenBuckets =
        [0.1, 0.25, 0.5, 1, 2, 3, 5, 8, 13, 21];

    private readonly Histogram<double> _firstToken;

    public RagStreamMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _firstToken = meter.CreateHistogram<double>(
            FirstTokenHistogramName,
            unit: "s",
            description: "RAG 回答ストリーム（/analysis/ask/stream）の要求受領から最初の token イベントを "
                       + "書き出すまでの秒数。NFR-02 の SLI「初回応答」はこれで判定する（応答完了時間ではない）。",
            tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = FirstTokenBuckets });
    }

    // 初回トークンまでの経過時間を 1 件計上する。
    //
    // 🔴 **token が 1 件も出なかったストリームでは呼ばない。** 0 を積むと
    // 「初回トークンが無かった」が「速かった」として分布の最下段へ入り、p95 が下振れする
    // （＝ SLO 違反を取りこぼす）。呼び出し側は最初の token を書き出した経路でだけ呼ぶ。
    public void RecordFirstToken(TimeSpan elapsed, string purpose) =>
        _firstToken.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>(PurposeTag, NormalizePurpose(purpose)));

    // 正準語彙のみ属性に載せ、未知値は other へ集約する。
    private static string NormalizePurpose(string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
            return ValueOther;
        return KnownPurposes.Contains(purpose) ? purpose.ToLowerInvariant() : ValueOther;
    }
}
