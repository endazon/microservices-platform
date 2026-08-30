using Knowledge.Contracts.Dtos;

namespace RetrievalService.Features.Search.Hybrid;

// FR-04, FR-17, UC-10, ADR-0035 決定 1 (#970): 二段検索の段④ —— **再ランク（統合）。**
//
// 🔴 **グラフの近接度を `Score` に混ぜない。** 類似度（`SearchResultDto.Score`）と近接度
// （`GraphProximity`）は尺度が違う。**混ぜた瞬間、出典に載る数字が「何の値か」を誰も言えなくなる**
// （利用者は出典のスコアを類似度として読む）。
//
// **したがって合成は並べ替えの中だけで行い、`SearchResultDto` は 1 件も書き換えない。**
//   - ① 由来のチャンク: 融合スコア（RRF）のまま
//   - ③ 由来のチャンク: ベクトルストアが返したコサイン類似度のまま
//
// 合成の式は本クラスの `Compose` 1 か所にだけ書く（重みつきの合成であることを明示する）。
public static class GraphRerank
{
    // 順位 → [0,1] の減衰。RRF と同じ平滑化定数を使う（同じ検索の中で 2 つの平滑化を持たない）。
    internal const int RankK = HybridSearchService.RrfK;

    // 統合順位（0 始まり）から検索側の成分を作る。rank=0 で 1.0、以降なだらかに減衰する。
    public static double RankScore(int rank) => (RankK + 1.0) / (RankK + rank + 1.0);

    // 🔴 **重みつきの合成。ここが唯一の混ぜ場所である。**
    public static double Compose(double rankScore, double proximity, double searchWeight, double graphWeight)
        => (searchWeight * rankScore) + (graphWeight * proximity);

    // ⚠️ **帰結として、1 つの応答の中で `Score` の尺度は 2 種類になる**（① は RRF の融合値、
    // ③ はコサイン類似度）。揃えるには ① 側を書き換えるしかなく、それは ADR-0035 決定 1 が
    // 禁じる「既存検索の変更」に当たる。**「Score は類似度であって近接度ではない」を守ることを
    // 優先する**（IADR-0263 決定 4）。
    //
    // ① と ③ を統合して並べ替える。**戻り値の要素は入力のインスタンスそのもの**であり、
    // `Score` を含む一切の項目を書き換えない。
    //
    // **③ 由来のチャンクの順位は |①| だけ後ろから始める。** 段③は後段であり、
    // その内部順位（コサイン類似度の降順）を保ったまま順位の軸を 1 本に保つ。
    // ここを 0 始まりにすると、③ の先頭が常に ① の先頭と同じ検索成分を持つことになり、
    // **「後段で拾った」という事実が順位から消える**。
    public static List<SearchResultDto> Merge(
        IReadOnlyList<SearchResultDto> primary,
        IReadOnlyList<SearchResultDto> graphDerived,
        IReadOnlyDictionary<Guid, double> proximityByDocument,
        double searchWeight,
        double graphWeight)
    {
        var composed = new List<(SearchResultDto Result, double Composite)>(primary.Count + graphDerived.Count);
        var seen = new HashSet<Guid>();

        for (var rank = 0; rank < primary.Count; rank++)
        {
            var hit = primary[rank];
            if (!seen.Add(hit.ChunkId))
                continue;
            composed.Add((hit, Score(hit, rank)));
        }

        for (var rank = 0; rank < graphDerived.Count; rank++)
        {
            var hit = graphDerived[rank];
            // 同じチャンクが両段に出たら ① 側を残す（融合スコアを保つ）。順位も ① 側で決まる。
            if (!seen.Add(hit.ChunkId))
                continue;
            composed.Add((hit, Score(hit, primary.Count + rank)));
        }

        // **安定ソート**（LINQ の OrderByDescending は安定）。同着は入力順＝上記の順位を保つ。
        return [.. composed.OrderByDescending(c => c.Composite).Select(c => c.Result)];

        double Score(SearchResultDto hit, int rank) => Compose(
            RankScore(rank),
            proximityByDocument.GetValueOrDefault(hit.DocumentId),
            searchWeight,
            graphWeight);
    }
}
