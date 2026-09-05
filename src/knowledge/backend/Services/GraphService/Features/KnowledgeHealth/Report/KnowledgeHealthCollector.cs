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
//      値ではない。**本文が変わったときだけ前進する時刻**を新設した（[[IADR-0353]]）。
//
// 計画の 7 指標のうち、本クラスが生産するのは **孤立文書数**と**陳腐化文書数**である。
// 残り 5 指標を**ここに足さない**理由は指標ごとに違う（[[IADR-0299]] 決定 1）:
//
// ★［2026-09-05 追記 / #1246］**`unresolved-links` と `edge-type-usage` を足した。**
// 従前ここには両者を足さない理由が書いてあったが、どちらも [[IADR-0389]] で解いた:
//   - `unresolved-links`  … 「解決失敗を捨てている」ままではなく、**リンク先の名前**を
//                           `document_link_targets` へ保存し、**ここで解決し直して**数える
//                           （決定 3。失敗を保存すると相手の改名・削除を取りこぼす）。
//   - `edge-type-usage`   … 観測値モデルに**内訳の軸**を足した（決定 1。IADR-0265 の先送りを解いた）。
//
// 生産するのは **4 指標**である。残り 3 指標を**ここに足さない**理由は指標ごとに違う：
//
// - `unsummarized-clusters` … クラスタリング・要約の実装が**リポジトリ全体で 0 件**。生産不能。
//                         **計画の裁定待ち**であり、実装側で先取りしない（#1246 が射程外と明記）。
// - `undefined-type-fallbacks` / `ingest-unknown-tags`
//                       … **既に生産されている**。宛先が観測値ではなく OTel カウンタであり、
//                         Grafana のパネルで観測する。
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

        // ★［2026-09-05 / #1246・[[IADR-0389]]］解決できないリンク数。しきい値は持たない。
        var unresolved = await CollectUnresolvedLinksAsync(ct);
        await reporter.ReportAsync(KnowledgeHealthIndicators.UnresolvedLinks, unresolved, ct: ct);

        logger.LogInformation(
            "ナレッジ健全性の観測値を報告した（indicator={Indicator} count={Count}）。"
            + "**件数には個人資料を含む** —— 除外は受け口が行う。",
            KnowledgeHealthIndicators.UnresolvedLinks, unresolved.Count);

        // ★［2026-09-05 / #1246・[[IADR-0389]]］辺の型ごとの使用件数。しきい値は持たない。
        var edgeTypeUsage = await CollectEdgeTypeUsageAsync(ct);
        await reporter.ReportAsync(KnowledgeHealthIndicators.EdgeTypeUsage, edgeTypeUsage, ct: ct);

        logger.LogInformation(
            "ナレッジ健全性の観測値を報告した（indicator={Indicator} count={Count}）。"
            + "**件数には個人資料を含む** —— 除外は受け口が行う。",
            KnowledgeHealthIndicators.EdgeTypeUsage, edgeTypeUsage.Count);
    }

    // 実際に使うしきい値。不正な構成では既定へ倒し、**倒したことを警告として残す**
    // （[[IADR-0353]] 決定 3。起動は落とさない —— 指標の都合で購読を止めない）。
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
    // （planning#494 決定 1・2 / [[IADR-0353]]）。
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

    // ★［2026-09-05 / #1246・[[IADR-0389]] 決定 2・3］解決できないリンク。
    //
    // 🔴 **保存された「失敗」を読むのではなく、いま解決し直す。** `document_link_targets` が
    // 持つのはリンク先の**名前**であり、それが文書 ID へ解決できるかは**相手の側の事情で変わる**
    // （相手が改名された・削除された）。取り込み時の判定を保存すると、
    // **相手が消えて他文書のリンクが壊れても、その文書が再取り込みされるまで数に現れない** ——
    // リンク切れを数える指標が、リンク切れの主因を取りこぼす（決定 3）。
    //
    // 🔴 **判定は `LinkTargetMatcher`（辺を張る側と同じ規則）に委ねる。** ここに規則を書き写すと、
    // 片方だけを直したときに「辺は張られないのに未解決にも数えられない」リンクが生まれる。
    //
    // 軸（`Dimension`）は **`not-found` / `ambiguous`**（決定 2）。どちらも辺は作られないが、
    // 運用の直し方が違う（作る vs 改名して一意にする）。
    internal async Task<IReadOnlyList<KnowledgeHealthObservation>> CollectUnresolvedLinksAsync(
        CancellationToken ct = default)
    {
        var targets = await db.DocumentLinkTargets.AsNoTracking()
            .Select(t => new { t.SourceDocumentId, t.Target })
            .ToListAsync(ct);
        if (targets.Count == 0)
            return [];

        // 突合の候補は**全文書の題名**である。孤立・陳腐化の収集と同じく全件を引く
        // （1 時間に 1 回であり、実データの行数に対して十分安い）。
        var documents = await db.Documents.AsNoTracking()
            .Select(d => new { d.DocumentId, d.Title, d.Attributes })
            .ToListAsync(ct);
        var candidates = documents
            .Select(d => new LinkTargetMatcher.TitleCandidate(d.DocumentId, d.Title))
            .ToList();
        var scopeOf = documents.ToDictionary(
            d => d.DocumentId,
            d => GraphDocumentScope.IsPrivateNote(d.Attributes) ? GraphDocumentScope.PrivateNote : null);

        // 同じ名前が多数の文書から参照される（実データではごく普通）。**名前ごとに 1 回だけ解く。**
        var matches = new Dictionary<string, LinkTargetMatcher.LinkTargetMatch>(StringComparer.Ordinal);

        var observations = new List<KnowledgeHealthObservation>();
        foreach (var t in targets)
        {
            if (!matches.TryGetValue(t.Target, out var match))
            {
                match = LinkTargetMatcher.Match(t.Target, candidates);
                matches[t.Target] = match;
            }
            if (match.IsResolved)
                continue;

            observations.Add(new KnowledgeHealthObservation(
                UnresolvedLinkKey(t.SourceDocumentId, t.Target),
                // 🔴 **リンクを書いている側の文書のスコープで判定する。** 相手は解決できていない
                // （＝どの文書か分からない）のだから、相手のスコープは原理的に引けない。
                // 個人資料に書かれたリンクの失敗を組織の指標へ混ぜない、が要点である。
                scopeOf.GetValueOrDefault(t.SourceDocumentId),
                match.Dimension));
        }

        return observations;
    }

    // 観測値の不透明な鍵。**リンク先の名前をそのまま渡さない。**
    //
    // 受け口は別サービスの DB であり、鍵は保存される。リンク先の名前は**文書の題名**であって、
    // 個人資料の題名でもあり得る。受け口は鍵を応答に出さないが、
    // **出さないことと持たないことは別である** —— 越境させる必要が無いので越境させない。
    // 同じ (文書, リンク先) が同じ鍵になれば重複排除の目的は満たされる。
    private static string UnresolvedLinkKey(Guid sourceDocumentId, string target)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(target));
        return $"{sourceDocumentId:N}:{Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    // ★［2026-09-05 / #1246・[[IADR-0389]] 決定 1・4］辺の型ごとの使用件数（ADR-0033 決定 9）。
    //
    // 観測値 1 件 ＝ **辺 1 本**であり、軸に**型名**を載せる。件数だけを送ると
    // 「辺の総数」にしかならず、どの型が使われているかが読めない（それが IADR-0265 の先送り）。
    //
    // 🔴 **軸に載せてよいのは辞書 `edge_types` の名前だけである**（実行時管理だが SC-09 の
    // 管理下にあり、無界ではない）。未定義の型はここへ来ない —— 抽出側が `related` へ丸めており
    // （ADR-0033 決定 3）、丸めた件数は別指標（`undefined-type-fallbacks`）が数えている。
    //
    // 🔴 **端点の**どちらか**が個人資料なら `private-note` を添える**（決定 4）。片側だけを見ると、
    // 個人資料から組織文書へ張った辺が組織の指標に混ざる。孤立文書数が「辺の相手のスコープを
    // 問わない」のは計画が定義した**文書**の性質だからで、辺そのものの帰属とは別の話である。
    internal async Task<IReadOnlyList<KnowledgeHealthObservation>> CollectEdgeTypeUsageAsync(
        CancellationToken ct = default)
    {
        var scopeOf = await db.Documents.AsNoTracking()
            .Select(d => new { d.DocumentId, d.Attributes })
            .ToDictionaryAsync(
                d => d.DocumentId,
                d => GraphDocumentScope.IsPrivateNote(d.Attributes),
                ct);

        var edges = await db.Edges.AsNoTracking()
            .Join(db.EdgeTypes.AsNoTracking(), e => e.EdgeTypeId, t => t.Id,
                (e, t) => new { e.Id, e.SourceDocumentId, e.TargetDocumentId, TypeName = t.Name })
            .ToListAsync(ct);

        return edges
            .Select(e => new KnowledgeHealthObservation(
                e.Id.ToString(),
                scopeOf.GetValueOrDefault(e.SourceDocumentId) || scopeOf.GetValueOrDefault(e.TargetDocumentId)
                    ? GraphDocumentScope.PrivateNote
                    : null,
                e.TypeName))
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
