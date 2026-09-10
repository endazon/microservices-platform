namespace GraphService.Domain.Clustering;

// FR-18, SC-10, ADR-0035 決定 3・5・6, ADR-0083 決定 2・3, [[IADR-0425]] 決定 4 (#1363):
// 「**クラスタ × 機密区分**」1 組ぶんの要約の**生成時刻**。
//
// 🔴 **要約の本文は持たない。** ADR-0035 決定 5 が別系統（別ストアまたは別コレクション）と定めており、
// ここが持つのは ADR-0083 決定 3 の起点（「**前回の要約生成時刻**（クラスタ × 機密区分ごと）」）だけである。
//
// 🔴 **現時点で書き手が居ない。** 要約生成バッチ（ADR-0035 決定 3 の `claude-opus-5` 呼び出し）は
// #1363 の受け入れ基準に無く、本作業の射程外である。**それでもこの表を今置く**のは、
// これが無いと ADR-0083 決定 3 の条件 2・3 が実装不能になり、「常に未要約」を焼き付けた生産者しか
// 作れないためである（[[IADR-0425]] 決定 4）。**空であることは「全クラスタが未要約」を意味し、
// それは現況として正しい。**
public class GraphClusterSummary
{
    public Guid ClusterId { get; private set; }

    // 機密区分（ClusterConfidentiality の 4 値）。
    public string Confidentiality { get; private set; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; private set; }

    public static GraphClusterSummary Create(
        Guid clusterId, string confidentiality, DateTimeOffset generatedAt) => new()
        {
            ClusterId = clusterId,
            Confidentiality = confidentiality,
            GeneratedAt = generatedAt,
        };
}

// ADR-0035 決定 3: コミュニティ要約の粒度は**機密区分単位**（4 通り）である。
// 「属性組み合わせ単位ではなく」という但し書きつきで確定しており、**粗い側から始める**のが決定の要点。
public static class ClusterConfidentiality
{
    public const string Public = "public";
    public const string Internal = "internal";
    public const string Confidential = "confidential";
    public const string Restricted = "restricted";

    // 🔴 **4 通りすべてが揃って初めて「要約済み」である**（ADR-0083 決定 2）。
    // 「1 つでも欠ければそのクラスタは未要約に数える」—— ADR-0035 決定 6 の
    // 「あるクラスタを作り直すときは 4 通りすべてを作り直す」と揃えるためである。
    public static readonly IReadOnlyList<string> All = [Public, Internal, Confidential, Restricted];
}
