namespace GraphService.Features.Clustering.Summarize;

// FR-17, FR-18, ADR-0035 決定 3・6, [[IADR-0430]] 決定 6 (#1395):
// クラスタ要約の生成バッチの**運用パラメータ**。
//
// 🔴 **既定はオフである。** 有効化するまで `ClusterSummaryHostedService` は登録されず、
// LLM の呼び出しは 1 回も起きない（`IADR-0083` の `DataSourceSync:Enabled=false` と同型）。
// **既定の描画（`helm template`）は本作業の前後でバイト一致である** —— chart は触っていない。
// 有効化は環境変数 `ClusterSummary__Enabled=true`（Helm では `services.graph.extraEnv`）。
//
// 🔴 **`ValidateOnStart` を付けない。** 不正値で起動を落とすと本サービスの `DocumentUpdated` /
// `DocumentDeleted` 購読ごと止まる（`KnowledgeHealthOptions` と同じ向き。要約の都合で購読を止めない）。
public sealed class ClusterSummaryOptions
{
    public const string SectionName = "ClusterSummary";

    // 1 周期で要約を作り直すクラスタの上限。**既定 20。**
    //
    // 1 クラスタ = 機密区分 4 通り = **LLM 呼び出し 4 回**である。初回稼働時は全クラスタが未要約で
    // あり、上限が無いと「クラスタ数 × 4 回」が 1 周期に集中する。費用は用途別計測（#1111）の
    // 枠内で見る建付けであり、**枠を持つ側が一度に使い切れる形にしない。**
    public const int DefaultMaxClustersPerRun = 20;

    // 既定オフ。**opt-in である。**
    public bool Enabled { get; set; }

    public int MaxClustersPerRun { get; set; } = DefaultMaxClustersPerRun;

    // 実際に使う値。不正値（0 以下）は既定へ倒す（起動は落とさない）。
    public int EffectiveMaxClustersPerRun
        => MaxClustersPerRun > 0 ? MaxClustersPerRun : DefaultMaxClustersPerRun;

    public bool HasInvalidMaxClustersPerRun => MaxClustersPerRun <= 0;
}
