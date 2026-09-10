namespace GraphService.Domain.Clustering;

// FR-10, FR-17, FR-18, SC-10, ADR-0035 決定 6, ADR-0083 決定 2・3, [[IADR-0425]] 決定 5 (#1363):
// **「未要約」の判定。** ADR-0083 決定 3 が 3 条件で定義している。
//
//   1. **要約が 1 つも無い**（機密区分 4 通りのいずれかが欠けている）
//   2. **クラスタ構成が変わった後に再生成されていない**
//   3. **所属文書が前回生成以降に更新された後に再生成されていない**
//
// 🔴 **しきい値は無い。** ADR-0083 決定 3 が「置かない。件数をそのまま出す」と明記している
// （ADR-0035 決定 6 が再生成の判定でしきい値を退けたのと同じ理由 ——
//  実データが無い段階では決め方が恣意的になる）。**起点は前回の要約生成時刻**である。
//
// 判定は**機密区分 4 通りのうち最も古い生成時刻**に対して行う。これは
// 「4 通りのうち 1 つでも古ければ未要約」と同値であり、ADR-0083 決定 2
// （「1 つでも欠ければ未要約」／ADR-0035 決定 6 の「4 通りすべてを作り直す」）と揃う。
internal static class UnsummarizedClusterRule
{
    // 内訳の軸に載せる理由。🔴 **基数が有界な語だけを載せる**（IKnowledgeHealthReporter の定め）。
    // 3 語で閉じており、ADR-0083 決定 3 の 3 条件と 1 対 1 である。
    public const string NoSummary = "no-summary";
    public const string CompositionChanged = "composition-changed";
    public const string DocumentsUpdated = "documents-updated";

    // 未要約なら理由、要約済みなら null。
    //
    // 複数の条件に当たるときは **1 → 2 → 3 の順で先勝ち**とする（内訳は 1 クラスタ 1 軸である）。
    // 条件 1 が最優先なのは、要約が欠けているクラスタでは 2・3 が測れない（起点が無い）ためである。
    public static string? Evaluate(
        DateTimeOffset compositionChangedAt,
        DateTimeOffset? latestMemberUpdatedAt,
        IReadOnlyDictionary<string, DateTimeOffset> summaryGeneratedAt)
    {
        // 条件 1: 機密区分 4 通りのいずれかが欠けている。
        if (ClusterConfidentiality.All.Any(c => !summaryGeneratedAt.ContainsKey(c)))
            return NoSummary;

        var oldest = ClusterConfidentiality.All.Min(c => summaryGeneratedAt[c]);

        // 条件 2: 構成変更が最終生成より後。
        if (compositionChangedAt > oldest)
            return CompositionChanged;

        // 条件 3: 所属文書の更新が最終生成より後。
        //
        // 🔴 **`UpdatedAt` で測る（`BodyUpdatedAt` ではない）。** 計画の字義は
        // 「そのクラスタに属する**文書のいずれかが前回生成以降に更新されたか**」（ADR-0035 決定 6）
        // であり、本文更新に限るとは書いていない。**安全側は「作り直す」側**である ——
        // 数え漏らすと運用者は古い要約を新しいと誤解するが、多く数えても余分な再生成が起きるだけで
        // 嘘は出ない。陳腐化文書数（IADR-0353）が `BodyUpdatedAt` を採ったのは
        // 「棚卸し作業そのものが指標を改善させる」逆向きの事故を塞ぐためであり、ここは向きが逆である。
        // 要約生成の費用が実測できたら計画へ環流する（作業仕様書 §未決事項 2）。
        if (latestMemberUpdatedAt is { } updated && updated > oldest)
            return DocumentsUpdated;

        return null;
    }
}
