namespace GraphService.Domain.Clustering;

// FR-17, FR-18, ADR-0035 決定 3・5, ADR-0083 決定 2, [[IADR-0430]] 決定 3 (#1395):
// 「**クラスタ × 機密区分**」1 組ぶんの要約の**本文**。
//
// 🔴 **`graph_cluster_summaries` には入れない。** [[IADR-0425]] 決定 4 が同表を
// 「生成時刻だけを持つ」として凍結しており、凍結記録の本文は後から書き換えない。
// **本表が `ADR-0035` 決定 5 の言う「別コレクション」である** ——
// 同決定が禁じたのは「要約を通常の文書として**既存の索引へ登録する**こと」であって、
// 別の表に持つことではない。索引化しないため、
//
//   - 第 1 段（ベクトル検索）の対象に入らない（決定 5 の理由 1）
//   - 個別の問いのときに要約が検索結果へ混ざらない（同 2 —— 出典が要約になって根拠を辿れなくなる）
//   - ABAC は「どの機密区分の要約を返すか」の 1 段で済む（同 3）
//
// **参照は主キー（ClusterId × Confidentiality）が兼ねる。** `graph_cluster_summaries` へ
// 参照列を足さない —— 同じ情報を 2 か所に持つことになる上、凍結した表の形に触れることになる。
public class GraphClusterSummaryBody
{
    public Guid ClusterId { get; private set; }

    // 機密区分（ClusterConfidentiality の 4 値）。
    public string Confidentiality { get; private set; } = string.Empty;

    // 要約の本文。**この区分の入力文書だけから作られている**（ClusterSummaryPrompt.Seal）。
    public string Body { get; private set; } = string.Empty;

    // 生成時刻。`graph_cluster_summaries` の同名欄と同じ値を書く
    // （判定はあちらが持つ。ここは本文とセットで残す監査用の写しである）。
    public DateTimeOffset GeneratedAt { get; private set; }

    public static GraphClusterSummaryBody Create(
        Guid clusterId, string confidentiality, string body, DateTimeOffset generatedAt) => new()
        {
            ClusterId = clusterId,
            Confidentiality = confidentiality,
            Body = body,
            GeneratedAt = generatedAt,
        };

    // 作り直し。**履歴は持たない**（`ADR-0035` 決定 6 の差分再生成は最新だけを見る）。
    public void Replace(string body, DateTimeOffset generatedAt)
    {
        Body = body;
        GeneratedAt = generatedAt;
    }
}
