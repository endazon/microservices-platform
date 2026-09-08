using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Domain;

// FR-07, FR-05, UC-02: 利用者が指定したデータ範囲を ABAC 許可スコープと交差させ、
// 検索へ渡す「実効アクセススコープ」を導出する。
//
// 🔴 **規則そのものはここに無い**（[[IADR-0415]] / #1340）。本体は共有点
// `Knowledge.Contracts.Dtos.ScopeNarrowing` に 1 つだけ在り、**受け口（RetrievalService）も
// 同じ関数を通る**。
//
// 従前この規則は本ファイルにだけ在り、受け口には無かった —— その非対称が #1340 の欠陥
// （利用者指定が ABAC の許可値集合を**広げていた**）の正体である。
// **規則が 1 か所にしか無いことが、もう 1 か所での欠落を許した。**
//
// ここに残すのは**呼び出し元の語彙で受ける入口**だけである ——
// `AnalysisDataRange` を知っているのは AiAnalysis だけであり、共有点へ持ち込むと
// 検索の受け口が分析の器を知ることになる。
public static class DataRangeScopeResolver
{
    public static AccessScope Resolve(AccessScopeResponse abac, AnalysisDataRange? range)
        => Resolve(abac, range?.AttributeFilters);

    // FR-04, SC-01, SC-08, #539: 対象範囲を**データ範囲の器から切り離して**受ける。
    //
    // SC-01（検索・質問）と SC-08（AI 分析）は「同じ『範囲を絞る』操作」であり、
    // **画面ごとに違う挙動になると利用者は操作を覚え直すことになる**（計画 L342・裁定 Q3）。
    // `/analysis/ask` は `AnalysisDataRange`（`Query` / `TopK` を持つ）を取らないので、
    // **交差の規則だけを共有する**。
    public static AccessScope Resolve(
        AccessScopeResponse abac, IReadOnlyDictionary<string, List<string>>? rangeFilters)
        => ScopeNarrowing.Resolve(abac, rangeFilters);
}
