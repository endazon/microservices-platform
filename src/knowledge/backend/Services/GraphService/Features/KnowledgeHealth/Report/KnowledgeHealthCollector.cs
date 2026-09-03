using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GraphService.Features.KnowledgeHealth.Report;

// FR-10, FR-17, FR-19, UC-05, SC-10, ADR-0002, ADR-0006, ADR-0033, ADR-0054,
// IADR-0265, [[IADR-0299]] (#443): ナレッジ健全性の観測値のうち、**本サービスが持つデータから
// 算出できるもの**を集めて受け口へ報告する。
//
// ## 生産するのは 2 指標だけである
//
// ★［2026-09-03 追記 / #1186］**`stale-documents` を足した。** 従前ここには
// 「判定材料（UpdatedAt）は揃うが、**しきい値が計画側で未確定**」と書いてあったが、
// **どちらも現況と食い違っていた**:
//   1. しきい値は planning#494 で **2026-08-29 に確定した**（180 日）。
//   2. 🔴 **「判定材料は揃う」も成立しない。** `UpdatedAt` はタグ・属性だけの更新でも前進し
//      （`Document.UpdateMetadata` / `Document.Update` が `Touch()` を呼ぶ）、これで数えると
//      **棚卸し作業そのものが指標を改善させる**。揃っていたのは**材料**（BodyHash）であって
//      値ではない。**本文が変わったときだけ前進する時刻**を新設した（[[IADR-0357]]）。
//
// 計画の 7 指標のうち、本クラスが生産するのは **孤立文書数**と**陳腐化文書数**である。
// 残り 5 指標を**ここに足さない**理由は指標ごとに違う（[[IADR-0299]] 決定 1）:
//
// - `unresolved-links`  … 解決失敗は LinkEdgeSynchronizer がログへ出して**捨てている**。
//                         永続化そのものが未設計である（曖昧一致を未解決に含めるかも未定義）。
// - `unsummarized-clusters` … クラスタリング・要約の実装が**リポジトリ全体で 0 件**。生産不能。
// - `edge-type-usage`   … 件数は引けるが、観測値モデルが「指標 1 つ＝件数 1 つ」であり
//                         **型別の内訳を表現できない**（IADR-0265 が先送り済み）。
// - `undefined-type-fallbacks` / `ingest-unknown-tags`
//                       … **既に生産されている**。宛先が観測値ではなく OTel カウンタであり、
//                         Grafana のパネルで観測する（本作業でパネルを新設した）。
public sealed class KnowledgeHealthCollector(
    GraphDbContext db,
    IKnowledgeHealthReporter reporter,
    IOptions<KnowledgeHealthOptions> options,
    TimeProvider clock,
    ILogger<KnowledgeHealthCollector> logger)
{
    // 1 周期分の仕事。**集めて、送る。**
    public async Task RunAsync(CancellationToken ct = default)
    {
        var orphans = await CollectOrphanDocumentsAsync(ct);

        // 🔴 **0 件でも送る。** 受け口はスナップショット置換であり、送らないと前回の件数が
        // 残り続ける（孤立を解消したのに数字が減らない）。ここを「無駄な送信の抑止」と
        // 読み替えて最適化してはならない。
        await reporter.ReportAsync(KnowledgeHealthIndicators.OrphanDocuments, orphans, ct: ct);

        logger.LogInformation(
            "ナレッジ健全性の観測値を報告した（indicator={Indicator} count={Count}）。"
            + "**件数には個人資料を含む** —— 除外は受け口が行う。",
            KnowledgeHealthIndicators.OrphanDocuments, orphans.Count);

        var thresholdDays = EffectiveStaleThresholdDays();
        var stale = await CollectStaleDocumentsAsync(thresholdDays, ct);

        // 🔴 **しきい値を添えて送る**（planning#494 決定 3「SC-10 には件数と現在のしきい値を併記する」）。
        // 添えるのは**実際に判定へ使った値**である —— 構成が不正で既定へ倒したときも、
        // 画面には倒した後の値が出る（嘘の数字を出さない）。
        await reporter.ReportAsync(
            KnowledgeHealthIndicators.StaleDocuments, stale, thresholdDays, ct);

        logger.LogInformation(
            "ナレッジ健全性の観測値を報告した（indicator={Indicator} count={Count} thresholdDays={Threshold}）。"
            + "**件数には個人資料を含む** —— 除外は受け口が行う。",
            KnowledgeHealthIndicators.StaleDocuments, stale.Count, thresholdDays);
    }

    // 実際に使うしきい値。不正な構成では既定へ倒し、**倒したことを警告として残す**
    // （[[IADR-0357]] 決定 3。起動は落とさない —— 指標の都合で購読を止めない）。
    // **1 周期に 1 回だけ評価する**（収集側へ引数で渡す。警告を二重に出さない）。
    internal int EffectiveStaleThresholdDays()
    {
        var opt = options.Value;
        if (opt.HasInvalidStaleDocumentThreshold)
            logger.LogWarning(
                "陳腐化文書数のしきい値の構成が不正である（{Configured} 日）。既定の {Default} 日へ倒した。"
                + "構成キーは {Key}:StaleDocumentThresholdDays である。",
                opt.StaleDocumentThresholdDays,
                KnowledgeHealthOptions.DefaultStaleDocumentThresholdDays,
                KnowledgeHealthOptions.SectionName);
        return opt.EffectiveStaleDocumentThresholdDays;
    }

    // 陳腐化文書 = **本文**が しきい値日数より前から更新されていない文書
    // （planning#494 決定 1・2 / [[IADR-0357]]）。
    //
    // 🔴 **`UpdatedAt` で数えない。** あちらはタグ・属性だけの更新でも前進するため、
    // **棚卸し作業そのもの（タグ整理）が指標を改善させる**。計画の言い方では
    // 「指標が自分の改善作業で消えるなら、それは測定ではない」。判定は BodyUpdatedAt が持つ。
    //
    // **境界は「しきい値ちょうどは陳腐でない」**。180 日ちょうど前の本文更新は含めず、
    // それより古いものだけを含める（`<` であって `<=` ではない）。
    internal async Task<IReadOnlyList<KnowledgeHealthObservation>> CollectStaleDocumentsAsync(
        int thresholdDays,
        CancellationToken ct = default)
    {
        var cutoff = clock.GetUtcNow().AddDays(-thresholdDays);

        // 孤立文書と同じく**件数ではなく行**を引く —— 各文書のスコープを添えて送るためである。
        var stale = await db.Documents.AsNoTracking()
            .Where(d => d.BodyUpdatedAt < cutoff)
            .Select(d => new { d.DocumentId, d.Attributes })
            .ToListAsync(ct);

        return stale
            .Select(d => new KnowledgeHealthObservation(
                d.DocumentId.ToString(),
                // 🔴 **集合帰属で判定する。「organization でない」と書いてはならない**
                // （理由は CollectOrphanDocumentsAsync の同じ箇所と同一）。
                GraphDocumentScope.IsPrivateNote(d.Attributes) ? GraphDocumentScope.PrivateNote : null))
            .ToList();
    }

    // 孤立文書 = **端点に自分を含む辺が 1 本も無い文書**。
    //
    // 🔴 **両端点を見る。** 対称型（related）は書き込み時に (min, max) へ正規化されるため
    // （IADR-0242 決定 9）、Source だけを見ると「相手の方が小さい ID だった」辺を取りこぼし、
    // 参照されている文書を孤立と数える。計画の定義「どの文書からも参照されず、**どの文書も
    // 参照していない**」の字義でもある。
    //
    // **辺の相手のスコープは問わない**（計画の字義どおり）。個人資料からリンクされた組織文書が
    // 孤立でなくなることの是非は計画へ裁定を依頼した（[[IADR-0299]] §結果 フォローアップ）。
    internal async Task<IReadOnlyList<KnowledgeHealthObservation>> CollectOrphanDocumentsAsync(
        CancellationToken ct = default)
    {
        // EF の 1 クエリ（リレーショナルでは WHERE NOT EXISTS）。件数ではなく行を引くのは、
        // **各文書のスコープを添えて送る**ためである（受け手が除外を強制する）。
        var orphans = await db.Documents.AsNoTracking()
            .Where(d => !db.Edges.Any(e =>
                e.SourceDocumentId == d.DocumentId || e.TargetDocumentId == d.DocumentId))
            .Select(d => new { d.DocumentId, d.Attributes })
            .ToListAsync(ct);

        return orphans
            .Select(d => new KnowledgeHealthObservation(
                d.DocumentId.ToString(),
                // 🔴 **集合帰属で判定する。「organization でない」と書いてはならない。**
                // `doc_scope` は 2026-08-22 新設で既存文書へ遡及付与しない（ADR-0054 §結果）。
                // 否定で書くと属性を持たない大多数が個人資料として送られ、受け手が全部落とし、
                // **孤立文書数が一斉に 0 になる**（「問題なし」と読める）。
                GraphDocumentScope.IsPrivateNote(d.Attributes) ? GraphDocumentScope.PrivateNote : null))
            .ToList();
    }
}
