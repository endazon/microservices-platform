namespace RetrievalService.Features.Search.Hybrid;

// FR-04, FR-14, FR-17, UC-10, ADR-0035 決定 2, ADR-0018 (#970): 二段検索の段の構成。
//
// 🔴 **`Enabled` の既定は false である。** ADR-0035 決定 2 が「**既定オフ・opt-in** とし、
// 既存 RAG と A/B 比較できる構成にする」と定めている。**既定値の変更は決定の変更である。**
public sealed class GraphExpansionOptions
{
    public const string SectionName = "GraphExpansion";

    // ADR-0034 決定 3 / ADR-0035 決定 2: ホップ数は既定 2・上限 3（探索内部の展開にも同じ裁定値を使う）。
    public const int DefaultHops = 2;
    public const int MaxHops = 3;

    // ADR-0035 決定 2「展開の起点はベクトル検索の上位 N 件のみ」の N。
    // 🔴 **実測値ではない**（実データが無い。#947a の次数上限と同じ理由）。A/B で測って決め直す。
    public const int DefaultSeedCount = 5;

    // 再ランクの重み。**近接度は `Score` に混ぜない**（GraphRerank 参照）ので、
    // ここで決めるのは**並べ替えの合成比**だけである。
    // 🔴 **実測値ではない。** w_graph=0.35 は「1 ホップの supersedes(1.0) が、統合順位で
    // おおよそ数件ぶん押し上がる」大きさとして置いた出発値である。
    public const double DefaultSearchWeight = 1.0;
    public const double DefaultGraphWeight = 0.35;

    public bool Enabled { get; set; }

    public int Hops { get; set; } = DefaultHops;

    public int SeedCount { get; set; } = DefaultSeedCount;

    public double SearchWeight { get; set; } = DefaultSearchWeight;

    public double GraphWeight { get; set; } = DefaultGraphWeight;

    // 範囲外・未知の値は**既定へ縮退する**（`SearchModes` / `SearchSorts` / `GraphThinning` と同じ作法）。
    // 例外にすると、構成を 1 つ間違えただけでサービスが起動しなくなる。
    // **黙って直さない** —— 直した事実はログに残す（静かな縮退を作らない）。
    public GraphExpansionOptions Normalize(ILogger? logger = null)
    {
        if (Hops is < 1 or > MaxHops)
        {
            logger?.LogWarning(
                "GraphExpansion:Hops {Configured} is out of range (1..{Max}); falling back to {Default}",
                Hops, MaxHops, DefaultHops);
            Hops = DefaultHops;
        }

        if (SeedCount < 1)
        {
            logger?.LogWarning(
                "GraphExpansion:SeedCount {Configured} is not positive; falling back to {Default}",
                SeedCount, DefaultSeedCount);
            SeedCount = DefaultSeedCount;
        }

        if (double.IsNaN(SearchWeight) || SearchWeight < 0)
        {
            logger?.LogWarning(
                "GraphExpansion:SearchWeight {Configured} is invalid; falling back to {Default}",
                SearchWeight, DefaultSearchWeight);
            SearchWeight = DefaultSearchWeight;
        }

        if (double.IsNaN(GraphWeight) || GraphWeight < 0)
        {
            logger?.LogWarning(
                "GraphExpansion:GraphWeight {Configured} is invalid; falling back to {Default}",
                GraphWeight, DefaultGraphWeight);
            GraphWeight = DefaultGraphWeight;
        }

        return this;
    }
}
