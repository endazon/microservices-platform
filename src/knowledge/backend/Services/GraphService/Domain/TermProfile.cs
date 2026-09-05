using System.Runtime.InteropServices;
using System.Text;
using Knowledge.Contracts.Indexing;

namespace GraphService.Domain;

// FR-18, ADR-0051 決定 1, IADR-0380 (#1244): **語の共起による文書の類似度（純粋・決定的）。**
//
// 類似度候補の供給元（ISimilarityCandidateSource）の材料である。外部 LLM・外部埋め込み・他サービスの
// いずれにも依存しない —— 同じ入力からは必ず同じ出力が出る。ADR-0051 決定 1 が「自システム内の演算」として
// 認めた越境（ABAC スコープを跨いで全文書を突き合わせる）は、ここで行う演算そのものである。
//
// ## 語の切り方
//
//   - CJK の連なり: 2-gram（`CjkBigramPayload.Encode`。検索側の索引と同じ切り方。1 文字の連なりは 1-gram）
//   - それ以外の文字・数字の連なり: 小文字化した語（2 文字以上）
//   - 表題の語は本文より重い（TitleWeight）
//   - 文書 1 件につき出現数の上位 MaxTerms 語だけを持つ（同数は語の順序で決める＝決定的）
//
// ## 似ている度合い
//
//   idf(t) = ln(1 + N / df(t))、w(d, t) = (1 + ln tf(d, t)) × idf(t)、コサイン。
//   🔴 **IDF を外してはならない。** 日本語の文書は「です」「ます」「こと」のような機能語の 2-gram を
//   ほぼ全件が共有し、素の共起だと**どの文書も互いに似て見える**。全件に共通する語ほど重みを落とすのは、
//   無関係な文書を候補にしないための唯一の仕掛けである（TermProfileTests / T-43 が変異で固定する）。
public static class TermProfile
{
    // 文書 1 件が持つ語の上限。全文書ぶんを 1 回の生成で読むため、行の大きさをここで抑える。
    public const int DefaultMaxTerms = 128;

    // 表題の語の重み（本文の 1 出現 = 1）。表題は文書の主題を最も短く表す。
    public const int TitleWeight = 3;

    // CJK 以外の語の最小長。1 文字の語（a・x・数字 1 桁）は主題を表さない。
    public const int MinLatinTermLength = 2;

    public sealed record RankedDocument(Guid DocumentId, double Score);

    // 表題と本文から語の出現数を作る。本文が無ければ表題だけで作る（縮退。null を返さない）。
    public static IReadOnlyDictionary<string, int> Extract(
        string? title, string? body, int maxTerms = DefaultMaxTerms)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        Accumulate(counts, title, TitleWeight);
        Accumulate(counts, body, 1);

        if (counts.Count <= maxTerms)
            return counts;

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(maxTerms)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    // 起点に似た文書を、スコアの降順で最大 limit 件返す。
    //
    //   - corpus は**起点を含む全文書**の出現数（IDF の母数 N はこの件数）
    //   - 起点自身は返さない。語を持たない文書・共有する語が無い文書も返さない
    //   - minScore 未満は落とす（無関係な文書を候補にしない）
    //   - 同点は文書 ID の昇順（決定的。順序が揺れると提案の並びが要求ごとに変わる）
    //
    // 🔴 **戻り値の件数・落とした件数を呼び出し側がログや応答へ出さないこと**（ADR-0051 決定 2）。
    public static IReadOnlyList<RankedDocument> Rank(
        Guid originDocumentId,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>> corpus,
        double minScore,
        int limit)
    {
        if (limit <= 0
            || !corpus.TryGetValue(originDocumentId, out var origin)
            || origin.Count == 0)
            return [];

        var n = corpus.Count;
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var terms in corpus.Values)
            foreach (var term in terms.Keys)
                CollectionsMarshal.GetValueRefOrAddDefault(df, term, out _)++;

        double Idf(string term) => Math.Log(1.0 + (double)n / df[term]);
        static double Tf(int count) => 1.0 + Math.Log(count);

        var originWeights = new Dictionary<string, double>(StringComparer.Ordinal);
        var originNorm = 0.0;
        foreach (var (term, count) in origin)
        {
            var w = Tf(count) * Idf(term);
            originWeights[term] = w;
            originNorm += w * w;
        }
        originNorm = Math.Sqrt(originNorm);
        if (originNorm == 0)
            return [];

        var ranked = new List<RankedDocument>();
        foreach (var (documentId, terms) in corpus)
        {
            if (documentId == originDocumentId || terms.Count == 0)
                continue;

            var dot = 0.0;
            var norm = 0.0;
            foreach (var (term, count) in terms)
            {
                var w = Tf(count) * Idf(term);
                norm += w * w;
                if (originWeights.TryGetValue(term, out var ow))
                    dot += ow * w;
            }

            if (dot <= 0)
                continue;

            var score = dot / (originNorm * Math.Sqrt(norm));
            if (score < minScore)
                continue;

            ranked.Add(new RankedDocument(documentId, score));
        }

        return ranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.DocumentId)
            .Take(limit)
            .ToList();
    }

    private static void Accumulate(Dictionary<string, int> counts, string? text, int weight)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        // CJK: 検索側と同じ 2-gram。CJK 以外は区切りとしてだけ働く。
        foreach (var gram in CjkBigramPayload.Encode(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            CollectionsMarshal.GetValueRefOrAddDefault(counts, gram, out _) += weight;

        // CJK 以外: 文字・数字の連なりを小文字化した語。
        var word = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (!CjkBigramPayload.IsCjk(rune) && Rune.IsLetterOrDigit(rune))
            {
                word.Append(Rune.ToLowerInvariant(rune).ToString());
                continue;
            }

            Flush(counts, word, weight);
        }

        Flush(counts, word, weight);
    }

    private static void Flush(Dictionary<string, int> counts, StringBuilder word, int weight)
    {
        if (word.Length >= MinLatinTermLength)
            CollectionsMarshal.GetValueRefOrAddDefault(counts, word.ToString(), out _) += weight;
        word.Clear();
    }
}
