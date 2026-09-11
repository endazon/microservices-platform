using GraphService.Domain.Clustering;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GraphService.Features.Clustering.Summarize;

// FR-17, FR-18, SC-10, SC-18, ADR-0035 決定 3・5・6・8, ADR-0051 決定 4, ADR-0083 決定 2・3,
// [[IADR-0430]] (#1395): **クラスタ要約の生成バッチ 1 周期ぶんの仕事。**
//
// `graph_cluster_summaries` の**書き手**である（[[IADR-0425]] 決定 4 が「書き手が来る前に置く」と
// した表に、ここで書き手が付く）。
//
// ## 何を作り直すか —— 判定は 1 か所から引く
//
// 🔴 **`UnsummarizedClusterRule.Evaluate` をそのまま使う。** 指標（`KnowledgeHealthCollector`）と
// 本ジョブが**同じ関数**を呼ぶ。`ADR-0035` 決定 6 の再生成条件（構成が変わった／所属文書が更新された）と
// `ADR-0083` 決定 3 の未要約条件は**同じものの裏表**であり、書き写せば片方が古くなる。
//
// ## 何を送るか —— 封が決める
//
// 送信は `ClusterSummaryPrompt`（封）を通る経路しか無い。封が落とすのは 2 つ:
// **個人資料**（`ADR-0035` 決定 8）と**その区分を超える文書**（同決定 6「機密区分ごとに入力文書集合が
// 異なる」）。したがって本クラスに「除外を忘れる」余地が無い。
//
// ## 1 実行 = 1 機密区分（`ADR-0051` との関係）
//
// 本バッチは**利用者スコープを一切使わない**。`ADR-0051` 決定 4 の「1 実行 = 1 利用者スコープ」は
// AI 提案生成（利用者ごとに結果が違うもの）の規律であり、クラスタ要約は `ADR-0035` 決定 3・7 が
// **利用者に紐づかない**と定めている。守る不変条件は「**1 生成 = 1 機密区分**」であり、封が型で強制する
// （[[IADR-0430]] 決定 2）。
public sealed class ClusterSummaryJob(
    GraphDbContext db,
    IClusterSummaryLlmClient llm,
    IOptions<ClusterSummaryOptions> options,
    TimeProvider clock,
    ILogger<ClusterSummaryJob> logger)
{
    public async Task<ClusterSummaryResult> RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var limit = EffectiveLimit();

        var clusters = await db.Clusters.AsNoTracking()
            .Select(c => new { c.ClusterId, c.CompositionChangedAt })
            .ToListAsync(ct);
        if (clusters.Count == 0)
            return ClusterSummaryResult.Empty;

        var members = await db.ClusterMembers.AsNoTracking()
            .Select(m => new { m.ClusterId, m.DocumentId })
            .ToListAsync(ct);
        var summaries = await db.ClusterSummaries.AsNoTracking()
            .Select(s => new { s.ClusterId, s.Confidentiality, s.GeneratedAt })
            .ToListAsync(ct);
        var documents = await db.Documents.AsNoTracking()
            .Select(d => new { d.DocumentId, d.Title, d.Attributes, d.UpdatedAt })
            .ToDictionaryAsync(d => d.DocumentId, ct);

        var membersByCluster = members
            .GroupBy(m => m.ClusterId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.DocumentId).ToList());
        var summaryOf = summaries
            .GroupBy(s => s.ClusterId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, DateTimeOffset>)g.ToDictionary(
                    s => s.Confidentiality, s => s.GeneratedAt, StringComparer.OrdinalIgnoreCase));

        var empty = (IReadOnlyDictionary<string, DateTimeOffset>)
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        // 🔴 **`ClusterId` の昇順で決定的に選ぶ。** 乱択も「最近のもの優先」も持たない
        // （[[IADR-0425]] 決定 2 と同じ向き —— 選ぶ順が周期ごとに変わると、上限に掛かった
        //  クラスタが永久に順番待ちになり得る）。
        var targets = clusters
            .Where(c => UnsummarizedClusterRule.Evaluate(
                c.CompositionChangedAt,
                membersByCluster.TryGetValue(c.ClusterId, out var ids)
                    ? ids.Select(id => documents.TryGetValue(id, out var d)
                            ? (DateTimeOffset?)d.UpdatedAt
                            : null).Max()
                    : null,
                summaryOf.GetValueOrDefault(c.ClusterId, empty)) is not null)
            .OrderBy(c => c.ClusterId)
            .Take(limit)
            .Select(c => c.ClusterId)
            .ToList();

        if (targets.Count == 0)
        {
            logger.LogDebug("要約を作り直すクラスタは無い（全クラスタが要約済みである）。");
            return ClusterSummaryResult.Empty;
        }

        var generated = 0;
        var failed = 0;
        var calls = 0;

        foreach (var clusterId in targets)
        {
            var docs = (membersByCluster.GetValueOrDefault(clusterId) ?? [])
                .Where(documents.ContainsKey)
                .Select(id => new ClusterMemberDocument(
                    id, documents[id].Title, documents[id].Attributes))
                .ToList();

            var (bodies, callCount) = await BuildAllTiersAsync(clusterId, docs, ct);
            calls += callCount;
            if (bodies is null)
            {
                // 🔴 **1 区分でも採れなければ 1 行も書かない**（[[IADR-0430]] 決定 4）。
                // `ADR-0035` 決定 6「あるクラスタを作り直すときは 4 通りすべてを作り直す」と
                // `ADR-0083` 決定 2「1 つでも欠ければ未要約」を、書き込みの粒度で守る。
                failed++;
                continue;
            }

            await PersistAsync(clusterId, bodies, now, ct);
            generated++;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "クラスタ要約を生成した（candidates={Candidates} generated={Generated} failed={Failed} "
            + "llmCalls={Calls}）。**個人資料は入力に含まない**（ADR-0035 決定 8）。",
            targets.Count, generated, failed, calls);

        return new ClusterSummaryResult(targets.Count, generated, failed, calls);
    }

    // 機密区分 4 通りぶんの本文を作る。**1 つでも採れなければ null**（部分的に返さない）。
    //
    // 🔴 **同じ入力集合には LLM を 1 回しか呼ばない。** 区分 X の入力は「順位が X 以下の構成員」で
    // あり、実データのように区分が 1 通りしか無ければ**上位 3 区分の入力集合は同一になる**。
    // 同じ集合へ 3 回問うても内容は同じであり、費用だけが 3 倍になる（[[IADR-0430]] 決定 7）。
    // **行は 4 通りぶん書く** —— 作り分けの単位は `ADR-0083` 決定 2 のまま変えない。
    private async Task<(Dictionary<string, string>? Bodies, int Calls)> BuildAllTiersAsync(
        Guid clusterId, IReadOnlyList<ClusterMemberDocument> docs, CancellationToken ct)
    {
        var bodies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byInput = new Dictionary<string, string>(StringComparer.Ordinal);
        var calls = 0;

        foreach (var tier in ClusterConfidentiality.All)
        {
            var prompt = ClusterSummaryPrompt.Seal(clusterId, tier, docs);
            if (prompt is null)
            {
                // その区分から見える文書が 1 件も無い。**要約は空である。**
                // 🔴 これは失敗ではない —— LLM を呼ばずに 4 通りを揃える唯一の正しい形である。
                // 空を書かないと、実データのように全文書が `internal` の場合に
                // `public` の行が永久に欠け、**そのクラスタが恒久的に未要約**になる
                // （`ADR-0083` 決定 2「1 つでも欠ければ未要約」。[[IADR-0430]] 決定 8）。
                bodies[tier] = string.Empty;
                continue;
            }

            var key = string.Join(",", prompt.Members.Select(m => m.DocumentId));
            if (byInput.TryGetValue(key, out var reused))
            {
                bodies[tier] = reused;
                continue;
            }

            calls++;
            var text = await llm.SummarizeAsync(prompt, ct);
            if (text is null)
            {
                logger.LogWarning(
                    "クラスタ要約を採れなかった（cluster={Cluster} tier={Tier}）。"
                    + "**このクラスタは 1 行も書かず未要約のまま残す。**",
                    clusterId, tier);
                return (null, calls);
            }

            byInput[key] = text;
            bodies[tier] = text;
        }

        return (bodies, calls);
    }

    // 生成時刻を `graph_cluster_summaries` へ、本文を `graph_cluster_summary_bodies` へ書く。
    // **既存行は上書きする**（作り直しであり、履歴は持たない —— `ADR-0035` 決定 6 の差分再生成は
    // 「最新の 4 通りが揃っているか」だけを見る）。
    private async Task PersistAsync(
        Guid clusterId,
        IReadOnlyDictionary<string, string> bodies,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var existingStamps = await db.ClusterSummaries
            .Where(s => s.ClusterId == clusterId).ToListAsync(ct);
        var existingBodies = await db.ClusterSummaryBodies
            .Where(s => s.ClusterId == clusterId).ToListAsync(ct);

        foreach (var tier in ClusterConfidentiality.All)
        {
            var stamp = existingStamps.FirstOrDefault(
                s => string.Equals(s.Confidentiality, tier, StringComparison.OrdinalIgnoreCase));
            if (stamp is null)
                db.ClusterSummaries.Add(GraphClusterSummary.Create(clusterId, tier, now));
            else
                stamp.MarkGenerated(now);

            var body = existingBodies.FirstOrDefault(
                s => string.Equals(s.Confidentiality, tier, StringComparison.OrdinalIgnoreCase));
            if (body is null)
                db.ClusterSummaryBodies.Add(
                    GraphClusterSummaryBody.Create(clusterId, tier, bodies[tier], now));
            else
                body.Replace(bodies[tier], now);
        }
    }

    // 実際に使う上限。不正な構成では既定へ倒し、**倒したことを警告として残す**
    // （`KnowledgeHealthCollector.EffectiveStaleThresholdDays` と同じ向き。起動は落とさない）。
    internal int EffectiveLimit()
    {
        var opt = options.Value;
        if (opt.HasInvalidMaxClustersPerRun)
            logger.LogWarning(
                "クラスタ要約の 1 周期上限の構成が不正である（{Configured}）。既定の {Default} へ倒した。"
                + "構成キーは {Key}:MaxClustersPerRun である。",
                opt.MaxClustersPerRun, ClusterSummaryOptions.DefaultMaxClustersPerRun,
                ClusterSummaryOptions.SectionName);
        return opt.EffectiveMaxClustersPerRun;
    }
}

// 1 周期の結果。**ログと検証のための値**であり、指標の生産者はこれを使わない
// （未要約クラスタ数は `KnowledgeHealthCollector` が永続化された行から数える）。
public sealed record ClusterSummaryResult(int Candidates, int Generated, int Failed, int LlmCalls)
{
    public static readonly ClusterSummaryResult Empty = new(0, 0, 0, 0);
}
