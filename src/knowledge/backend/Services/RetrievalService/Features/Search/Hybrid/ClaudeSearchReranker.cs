using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Observability;
using RetrievalService.Common.Observability;
using RetrievalService.Domain.Ports;

namespace RetrievalService.Features.Search.Hybrid;

// FR-03, FR-04, FR-10, FR-11, FR-19, SC-02, UC-01, ADR-0127 決定 3・4・7, ADR-0010, ADR-0044 決定 1,
// ADR-0076 決定 4, [[IADR-0498]] (#1746 段 S2): **Claude による再順位付けの段。**
//
// 検索サービスの唯一の出口（`HybridSearchService.FinishAsync`）から呼ばれる。RAG 回答も SC-02 も MCP のツールも
// 同じ検索を通るので、**この 1 か所で全部に効く**（AI 分析側には段を置かない —— 二重に掛けない）。
//
// ```text
// 候補（ABAC 後・露出 search 後・切り詰め前）
//   → 掛けるか（hybrid / keyword・relevance・合成監視でない）
//   → 窓（先頭 N 件）→ 送れる候補（ai_input が許すもの）だけを選ぶ（2 件未満なら呼ばない）
//   → 送る候補の最も高い機密区分で、用途 rerank としてゲートウェイの /complete を 1 回呼ぶ
//   → 出力の番号を検証して、送れる候補の位置の間だけで並べ替える
// ```
//
// 🔴 **失敗は元の順で返す（fail-open は並びについてだけ）。** 時間切れ・輸送の失敗・越境拒否（`Sent=false`）・
// `refusal`・解釈できない出力のどれでも、**ゲートウェイ以外の送信先へは倒さない**（そういう枝を持たない）。
// 縮退は計器（`search.rerank.total{result=degraded}`）と警告ログに残す。
// 🔴 **利用者の取り消しは取り消しのまま上げる**（検索そのものを止めるのは呼び出し側の意思である）。
public sealed class ClaudeSearchReranker(
    IRerankCompletionClient llm,
    SearchRerankOptions options,
    RerankMetrics metrics,
    ILogger<ClaudeSearchReranker> logger,
    IHttpContextAccessor? httpContextAccessor = null) : ISearchReranker
{
    public async Task<List<SearchResultDto>> RerankAsync(
        SearchRequest request, string sort, List<SearchResultDto> candidates, CancellationToken ct = default)
    {
        // 段は無効なら DI に登録されない。ここは構成を直接組んだ呼び出し（試験）への保険である。
        if (!options.Enabled || candidates.Count < 2)
            return Skip(candidates, options.Enabled ? RerankMetrics.TooFewCandidates : null);

        // [[IADR-0498]] 決定 2: 日時順は並べ替え後に日時で並べ直すので、関連度の並べ替えは意味を持たない。
        if (sort != SearchSorts.Relevance)
            return Skip(candidates, RerankMetrics.SortUpdated);

        // [[IADR-0498]] 決定 2: 意味検索のモードには語彙索引の文書が現れず（ADR-0127 決定 2）、並びは既に意味で決まっている。
        if (SearchModes.Normalize(request.Mode) == SearchModes.Semantic)
            return Skip(candidates, RerankMetrics.SemanticMode);

        // NFR-02, ADR-0076 決定 4, [[IADR-0378]]: 🔴 **合成監視の検索に費用を出さない。**
        // 外周（BFF）・AI 分析が付けた内周の標識を読む（RAG 回答の `SuppressLlmForSynthetic` と同じ判定）。
        if (SyntheticTraffic.IsSyntheticInternalRequest(httpContextAccessor?.HttpContext?.Request))
            return Skip(candidates, RerankMetrics.Synthetic);

        // [[IADR-0498]] 決定 3・4: 窓の中で、AI の入力に含めてよい候補だけを送る。
        var window = Math.Min(options.CandidateCount, candidates.Count);
        var slots = new List<int>(window);
        for (var i = 0; i < window; i++)
            if (RerankPrompt.IsSendable(candidates[i]))
                slots.Add(i);

        if (slots.Count < 2)
            return Skip(candidates, RerankMetrics.TooFewCandidates);

        var sent = slots.Select(i => candidates[i]).ToList();

        // FR-11, ADR-0127 決定 3・4: 越境は**送る候補の**最も高い機密区分で判定させる（未指定・未知は restricted）。
        var body = new CompletionApiRequest(
            RerankPrompt.Build(request.Query, sent, options.MaxCharsPerCandidate),
            MaxTokens: options.MaxOutputTokens,
            Model: null,
            Confidentiality: RerankPrompt.HighestConfidentiality(sent),
            Purpose: SearchRerankOptions.Purpose);

        CompletionApiResponse response;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                response = await llm.CompleteAsync(body, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Degrade(candidates, RerankMetrics.Timeout, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Degrade(candidates, RerankMetrics.Transport, ex);
            }
        }

        // ゲートウェイが送らなかった（越境拒否・プロバイダ未登録・上流の不調）。**別の送信先を試さない。**
        if (!response.Sent)
            return Degrade(candidates, RerankMetrics.NotSent, null);

        // ADR-0025, [[IADR-0104]]: 送信は成立したがモデルが拒否した。本文は空である。
        if (CompletionStopReasons.IsRefusal(response.StopReason))
            return Degrade(candidates, RerankMetrics.Refusal, null);

        var order = RerankPrompt.ParseOrder(response.Text, sent.Count);
        if (order is null)
            return Degrade(candidates, RerankMetrics.Unparseable, null);

        metrics.Record(RerankMetrics.Applied, RerankMetrics.None);
        return RerankPrompt.Merge(candidates, slots, [.. order.Select(i => sent[i])]);
    }

    private List<SearchResultDto> Skip(List<SearchResultDto> candidates, string? reason)
    {
        if (reason is not null)
            metrics.Record(RerankMetrics.Skipped, reason);
        return candidates;
    }

    // 🔴 **元の順で返す。** 検索語・文書の本文はログに出さない（理由だけ）。
    private List<SearchResultDto> Degrade(List<SearchResultDto> candidates, string reason, Exception? ex)
    {
        metrics.Record(RerankMetrics.Degraded, reason);
        logger.LogWarning(ex,
            "Search rerank degraded ({Reason}); returning the original order of {Count} candidates",
            reason, candidates.Count);
        return candidates;
    }
}
