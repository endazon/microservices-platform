namespace RetrievalService.Features.Search.Hybrid;

// FR-03, FR-04, SC-02, ADR-0127 決定 3, [[IADR-0498]] 決定 2・3・7・11 (#1746 段 S2): 再順位付けの段の構成。
//
// 🔴 **`Enabled` の既定は false である。** 有効にすると検索のたびに Claude の費用が生じ、
// `restricted` と機密区分が未指定・未知の本文がティア B へ出る（ADR-0127 決定 4 の受け入れたリスク）。
// 費用と越境の両方を伴う段を、構成の欠落で黙って有効にしない。有効にするのは構成 1 つ（`Rerank__Enabled=true`）。
// 無効のときは段の型を DI に登録しない（`Program.cs`。二段検索と同じ「着脱可能な段」）。
public sealed class SearchRerankOptions
{
    public const string SectionName = "Rerank";

    // ゲートウェイの用途（`Llm:Routing:PurposeModels` のキー）。回答生成（`rag-answer`）と分けて費用を計上する
    // （ADR-0127 決定 3・ADR-0044 決定 1）。🔴 ゲートウェイはこの用途を機密区分によらず ZDR 必須として扱う。
    public const string Purpose = "rerank";

    // 候補の幅（窓）。露出で落とした後の先頭 N 件だけを送る。
    // 🔴 **実測値ではない。** RAG の既定 topK 5 の 4 倍（検索の候補幅 `topK * 4` と同じ比）・SC-02 の 1 ページ（既定 10）の 2 倍として置いた。
    // 費用はほぼ件数に比例する（IADR-0498 §費用）。S3 の nDCG@10 の測定で見直す。
    public const int DefaultCandidateCount = 20;
    public const int MinCandidateCount = 2;
    public const int MaxCandidateCount = 50;

    // 1 件あたりの本文の上限（字）。日本語は 1 字 ≒ 1 トークンを上限に見る。
    public const int DefaultMaxCharsPerCandidate = 400;
    public const int MinMaxCharsPerCandidate = 50;
    public const int MaxMaxCharsPerCandidate = 2000;

    // 1 回の呼び出しの期限（秒）。超えたら元の順で返す（検索そのものは止めない）。
    public const int DefaultTimeoutSeconds = 8;
    public const int MaxTimeoutSeconds = 30;

    // 出力の上限（トークン）。番号 20 個の JSON は 100 トークンに満たない。
    // ［2026-10-10 / #1875・IADR-0529 決定 5］割当が `claude-haiku-5-5` になり、thinking が既定で有効（無効にできない）に
    // なった。この上限は**思考と本文の合算**に使われる（従前の `claude-haiku-4-5` は思考せず本文だけだった）。
    // ゲートウェイは rerank に effort low を付けて思考を絞るが、上限を 512 → 1024 へ上げて思考の余地を残す。
    // 上限に達すると本文の JSON が切れて元の順へ縮退するため（検索は止まらない）、余地を持つ側に倒す。
    // 費用の上限は 1 回 1024 × $0.50/1M ≒ $0.0005 で、従前（512 × $5/1M ≒ $0.0026）より低い。
    // 遅延は期限（既定 8 秒）が抑える —— 期限に掛かれば同じく元の順へ縮退する。
    public const int DefaultMaxOutputTokens = 1024;
    public const int MinMaxOutputTokens = 64;
    public const int MaxMaxOutputTokens = 2048;

    public bool Enabled { get; set; }

    public int CandidateCount { get; set; } = DefaultCandidateCount;

    public int MaxCharsPerCandidate { get; set; } = DefaultMaxCharsPerCandidate;

    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    public int MaxOutputTokens { get; set; } = DefaultMaxOutputTokens;

    // 範囲外は**既定へ縮退する**（`GraphExpansionOptions` と同じ作法）。例外にすると構成を 1 つ間違えただけで
    // 検索サービスが起動しなくなる。**黙って直さない** —— 直した事実はログに残す。
    public SearchRerankOptions Normalize(ILogger? logger = null)
    {
        CandidateCount = Clamp(nameof(CandidateCount), CandidateCount,
            MinCandidateCount, MaxCandidateCount, DefaultCandidateCount, logger);
        MaxCharsPerCandidate = Clamp(nameof(MaxCharsPerCandidate), MaxCharsPerCandidate,
            MinMaxCharsPerCandidate, MaxMaxCharsPerCandidate, DefaultMaxCharsPerCandidate, logger);
        TimeoutSeconds = Clamp(nameof(TimeoutSeconds), TimeoutSeconds,
            1, MaxTimeoutSeconds, DefaultTimeoutSeconds, logger);
        MaxOutputTokens = Clamp(nameof(MaxOutputTokens), MaxOutputTokens,
            MinMaxOutputTokens, MaxMaxOutputTokens, DefaultMaxOutputTokens, logger);
        return this;
    }

    private static int Clamp(string name, int value, int min, int max, int fallback, ILogger? logger)
    {
        if (value >= min && value <= max)
            return value;

        logger?.LogWarning(
            "Rerank:{Name} {Configured} is out of range ({Min}..{Max}); falling back to {Default}",
            name, value, min, max, fallback);
        return fallback;
    }
}
