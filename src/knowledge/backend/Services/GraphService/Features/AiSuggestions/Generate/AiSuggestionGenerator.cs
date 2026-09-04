using GraphService.Common.Observability;
using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using GraphService.Domain.Ports;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Features.AiSuggestions.Generate;

// FR-18, UC-10, ADR-0034 決定 5, ADR-0051 決定 1〜4, ADR-0033 決定 7, IADR-0266 (#915):
// **AI 提案（リンク候補・タグ候補）の生成。**
//
// ADR-0051 決定 3 が定めた順序をそのまま並べたものである。
//
//   [1] 起点文書   FindNode → AuthorizedNode.Authorize（不可なら「見つからない」。存在秘匿）
//   [2] 類似度     ISimilarityCandidateSource（★全文書横断でよい。決定 1）
//   [3] 候補列挙   IGraphStore.EnumerateAuthorizedCandidatesAsync（★**ここで絞る**。決定 3）
//   [4] 封         SuggestionPrompt.Seal（述語を再適用。多層防御）
//   [5] LLM        ISuggestionLlmClient.ProposeAsync（封しか受け取らない）
//   [6] 取り込み   許可済み候補集合に無い対象は捨てる（決定 5）→ pending で永続
//
// 🔴 **実行タイミングは「利用者リクエスト時」である**（ADR-0051 決定 4 は実装設計へ委ねた）。
// 計画側の要件は **各実行が 1 利用者のスコープに閉じていること**ただ 1 つであり、要求の
// HttpContext から解決したスコープが**その実行の唯一のスコープ**になる形で満たす。
// 定期バッチを採らないため、決定 4 後段の「生成結果の失効条件」を新設する必要が無い
// —— 提示（一覧）が**その時点のスコープで再判定**する。
//
// 🔴 **不足分を追加で引き直さない**（IADR-0266 決定 4）。ADR-0051 は応答時間の相関を禁じていないが
// 「範囲の外に置いた」と明記して実装側へ判断を残している。追加照会を採らなければ、フィルタで
// 落ちた件数と後段の処理量が相関しない。**費用ゼロで側チャネルを 1 本閉じられるので閉じる。**
//
// 🔴 **タグ提案は SC-09 のタグ辞書に定義済みの値に限る**（ADR-0063 決定 2 / IADR-0364 決定 2 / #1014）。
// 辞書（`ITagDictionaryReader`。権威は DocumentService）を辺の型と同じく LLM に選ばせる値集合として
// 渡し、**返ってきた値を [6] で突き合わせて辞書外を落とす**。辞書が引けなければタグ提案を作らない
// （fail-closed）。落とした件数は `TagSuggestionDropMetrics` が数える（0 が正常）。
public sealed class AiSuggestionGenerator(
    IGraphStore store,
    ISimilarityCandidateSource similarity,
    ISuggestionLlmClient llm,
    ITagDictionaryReader tagDictionary,
    TagSuggestionDropMetrics dropMetrics,
    GraphDbContext db,
    TimeProvider clock)
{
    // 類似度側へ要求する件数。**スコープで落ちるぶんを見込んで多めに引く**が、
    // **落ちた数に応じて引き直しはしない**（上記の側チャネル）。
    public const int SimilarityFetchCount = 50;

    // LLM へ渡す候補の上限。プロンプト長と提案の質の折衷であり、実測値ではない。
    public const int MaxCandidates = 10;

    // 戻り値 null は「起点文書が見つからない**または**見えない」を表す。
    // 🔴 **呼び出し側は両者を区別せず 404 に倒すこと**（ADR-0034 決定 2）。
    public async Task<IReadOnlyList<AiSuggestion>?> GenerateAsync(
        Guid originDocumentId, AccessScopeResponse scope, CancellationToken ct = default)
    {
        // FR-05: deny-by-default。スコープが解決できていなければ何もしない（LLM も呼ばない）。
        if (!scope.Granted)
            return null;

        // [1] 起点。**見えなければ「見つからない」**（403 は「権限が無いだけで存在はする」を漏らす）。
        var originNode = await store.FindNodeAsync(originDocumentId, ct);
        if (originNode is null)
            return null;
        var origin = AuthorizedNode.Authorize(originNode, scope);
        if (origin is null)
            return null;

        // [2] 類似度。**ABAC スコープを跨いでよい**（ADR-0051 決定 1）。自システム内のベクトル演算であり
        // 本文がどこへも出ない。**ここで返る候補にはスコープ外が混じり得る。**
        var similar = await similarity.FindSimilarAsync(originDocumentId, SimilarityFetchCount, ct);
        if (similar.Count == 0)
            return [];

        // 同一 ID が複数返っても最初（＝最も高い類似度）を採る。
        var scores = new Dictionary<Guid, double>();
        foreach (var c in similar)
            if (!scores.ContainsKey(c.DocumentId))
                scores[c.DocumentId] = c.Score;

        // [3] 🔴 **候補列挙の段でスコープ述語を適用する。** 返るのは AuthorizedNode だけであり、
        // 「LLM へ渡してから捨てる」形はここから先で書けない。
        var authorized = await store.EnumerateAuthorizedCandidatesAsync(
            originDocumentId, scores.Keys, scope, ct);
        if (authorized.Count == 0)
            return [];

        var candidates = authorized
            .OrderByDescending(n => scores.GetValueOrDefault(n.DocumentId))
            .ThenBy(n => n.DocumentId)
            .Take(MaxCandidates)
            .ToList();

        // ADR-0033 決定 3: 辺の型は実行時辞書である。**LLM に選ばせる値集合を渡す。**
        var types = await db.EdgeTypes.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);

        // ADR-0063 決定 2 (#1014): タグ辞書も**実行時辞書**として LLM へ渡す。**null ＝引けなかった**
        // （空集合とは別）。引けなければ値集合を空で渡し、[6] でタグ提案を全件落とす（fail-closed）。
        var dictionary = await tagDictionary.ReadNamesAsync(ct);
        var tagNames = dictionary is null
            ? []
            : dictionary.OrderBy(n => n, StringComparer.Ordinal).ToList();

        // [4] 封。ここを通らない文字列を LLM へ送る経路は無い。
        var prompt = SuggestionPrompt.Seal(
            origin, candidates, types.Select(t => t.Name).ToList(), tagNames, scope);
        if (prompt is null)
            return [];

        // [5] LLM。
        var proposals = await llm.ProposeAsync(prompt, ct);
        if (proposals.Count == 0)
            return [];

        // [6] 取り込み。
        return await PersistAsync(originDocumentId, candidates, types, dictionary, proposals, ct);
    }

    // FR-18, ADR-0033 決定 7, IADR-0266 決定 5: LLM の応答を提案として実体化する。
    //
    // 🔴 **許可済み候補集合に無い対象は捨てる。** 渡していない ID を LLM が返しても
    // 提案にならない（幻覚・復唱で越境が実体化する経路を塞ぐ）。
    //
    // 🔴 **辞書に無いタグ値も同じく捨てる**（ADR-0063 決定 2「辞書外の値を持つ提案は生成しない」）。
    // `dictionary` が null（引けなかった）ならタグ提案は 1 件も作らない。**比較は Ordinal** ——
    // DocumentService の `TagResolver.ToIdsAsync` と同じ比較でないと、生成段で通した値が承認段で落ちる。
    private async Task<IReadOnlyList<AiSuggestion>> PersistAsync(
        Guid originDocumentId,
        IReadOnlyList<AuthorizedNode> candidates,
        IReadOnlyList<EdgeType> types,
        IReadOnlySet<string>? dictionary,
        IReadOnlyList<LlmSuggestionProposal> proposals,
        CancellationToken ct)
    {
        var allowed = candidates.Select(c => c.DocumentId).ToHashSet();
        var byName = types.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        // ADR-0033 決定 3: 未定義型は既定型（related）へフォールバックする。
        byName.TryGetValue(EdgeTypeSeed.DefaultTypeName, out var fallback);

        // 同じ起点に既にあるタグ提案（状態を問わない）は作り直さない。
        // リンク側の重複除外は候補列挙の段で済んでいる。
        var existingTags = await db.AiSuggestions.AsNoTracking()
            .Where(s => s.Kind == SuggestionKind.Tag && s.SourceDocumentId == originDocumentId)
            .Select(s => s.TagValue)
            .ToListAsync(ct);
        var tagsSeen = new HashSet<string>(
            existingTags.Where(v => v is not null).Select(v => v!), StringComparer.OrdinalIgnoreCase);

        var now = clock.GetUtcNow();
        var created = new List<AiSuggestion>();
        var linksSeen = new HashSet<Guid>();

        foreach (var p in proposals)
        {
            if (p.Kind == SuggestionKind.Link)
            {
                if (p.TargetDocumentId is not { } target || !allowed.Contains(target))
                    continue;
                if (!linksSeen.Add(target))
                    continue;

                var type = p.EdgeTypeName is not null && byName.TryGetValue(p.EdgeTypeName, out var t)
                    ? t
                    : fallback;
                // seed 前で辞書が空なら作らない（存在しない型 ID の辺を後で作れないため）。
                if (type is null)
                    continue;

                created.Add(AiSuggestion.CreateLink(
                    originDocumentId, target, type.Id, p.Rationale, now));
            }
            else if (p.Kind == SuggestionKind.Tag)
            {
                var value = p.TagValue?.Trim();
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                // ★辞書の値域★（#1014）。辞書が分からなければ全件、分かっていれば辞書外を落とす。
                if (dictionary is null)
                {
                    dropMetrics.RecordDropped(TagSuggestionDropMetrics.DictionaryUnavailable);
                    continue;
                }
                if (!dictionary.Contains(value))
                {
                    dropMetrics.RecordDropped(TagSuggestionDropMetrics.OutOfDictionary);
                    continue;
                }

                if (!tagsSeen.Add(value))
                    continue;

                created.Add(AiSuggestion.CreateTag(originDocumentId, value, p.Rationale, now));
            }
        }

        if (created.Count == 0)
            return [];

        // ADR-0033 決定 7: **生成された提案はすべて pending で入る**（AiSuggestion のファクトリが強制する）。
        db.AiSuggestions.AddRange(created);
        await db.SaveChangesAsync(ct);
        return created;
    }
}
