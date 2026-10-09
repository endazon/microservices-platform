using GraphService.Domain;
using GraphService.Common.Observability;
using GraphService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.GraphDocuments.Sync;

// FR-17, UC-10, ADR-0033 決定 3・4・6・8, IADR-0281 (#912): 本文から抽出したリンクを辺へ反映する。
//
// ## 流れ
//
//   [1] 抽出   ObsidianLinkParser（純粋。Domain）
//   [2] 型解決 EdgeTypeResolver（純粋。Domain）＋ 実行時辞書 edge_types
//   [3] 名前解決 リンク先の名前 → 文書 ID（graph_documents の**複製** Title。鮮度契約 1）
//   [4] 差分   provenance=auto かつ ExtractedFrom=当該文書 の辺だけを置換（ADR-0033 決定 6）
//
// 🔴 **SaveChanges を呼ばない。** 呼び出し元（GraphDocumentSyncConsumer）が 1 回だけ保存し、
// ノード upsert・却下解除・辺の差分を**同一トランザクション**に収める。ここで保存すると
// 「辺は入ったがノードの属性は入らなかった」という中途半端な状態が作れてしまう。
//
// 🔴 **層は合成ルート側（Features/）である。** IADR-0280 決定 2 の写像では調整サービスは Application だが、
// 本クラスが触る `GraphDbContext` / `Edge` / `EdgeType` は段 2 の移送が済んでおらず、まだ Api 側に
// ある（同 決定 1 の段階計画）。**依存の向きに従い、依存先と同じ層に置く。** 段 2 で
// Persistence / Domain が移るときに一緒に移す。
//
// **段は 3 段目（`Sync/`）である**（#1094 / IADR-0350）。使う操作が `GraphDocuments/Sync` の 1 つ
// だけなので ADR-0068 決定 2 が下ろす。**層の理由（上段）と段の理由は別であり、段を下げても
// 層は動かない**（IADR-0349 決定 3）。
public sealed class LinkEdgeSynchronizer(
    GraphDbContext db,
    EdgeTypeFallbackMetrics metrics,
    ILogger<LinkEdgeSynchronizer> logger)
{
    // ADR-0033 決定 5: アンカー欄は 200 字（GraphDbContext の HasMaxLength と同値）。
    // 長い見出しは**切り詰める**（辺を作らない側に倒すと、見出し付きリンクだけが静かに消える）。
    private const int MaxAnchorLength = 200;

    // 差分適用の結果。呼び出し元のログのために返す（保存は呼び出し元が行う）。
    //
    // `Unresolved` — **本文が指しているのに文書 ID へ解決できなかったリンク先の数**
    //   （不在・曖昧の合計）。[[IADR-0389]] (#1246) で足した。
    //   🔴 **これは指標の値ではない。** 指標（`unresolved-links`）は `document_link_targets` から
    //   **収集のたびに解決し直して**数える —— ここでの値は取り込んだ瞬間の写像であり、
    //   相手が後から改名・削除されても動かない。ログの手掛かりとしてだけ返す。
    public readonly record struct SyncResult(int Extracted, int Added, int Removed, int Unresolved, int Claimed = 0);

    // [[IADR-0521]] (#1396): 名前の解決の候補を**未保存の値で上書きする**。
    // 同じ保存の中で新規・改名・撤収した文書は、DB の題名がまだ古い（または行が無い・まだ在る）。
    // `Title` が null は「候補から外す」（撤収・削除）。
    public readonly record struct CandidateOverride(Guid DocumentId, string? Title);

    public Task<SyncResult> SyncAsync(Guid documentId, string content, CancellationToken ct = default)
        => SyncAsync(documentId, content, pending: null, ct);

    public async Task<SyncResult> SyncAsync(
        Guid documentId, string content, CandidateOverride? pending, CancellationToken ct = default)
    {
        var links = ObsidianLinkParser.Parse(content);

        // [3b] FR-10, SC-10, [[IADR-0389]] (#1246): **リンク先の名前を全量置換で保存する。**
        // 未解決リンク数の材料である。🔴 **解決できたものも保存する** ——
        // 相手が後から改名・削除されると解決できなくなるため、
        // 「いま解決できた」を根拠に捨てると、その壊れ方を永久に取りこぼす。
        // ［[[IADR-0521]]］構文の別・明示型・アンカーも保存する（後着の相手へ本文を読まずに辺を張るため）。
        await ReplaceLinkTargetsAsync(documentId, links, ct);

        return await ApplyAsync(documentId, links, pending, ct);
    }

    // [[IADR-0521]] (#1396): **保存済みのリンクから**起点の辺を作り直す（本文は読まない）。
    //
    // 起点の本文が変わらなくても、**相手の側の事情で**解決が変わる —— 相手が後から届いた（先に届いた
    // 文書の `[[相手]]` が未解決のまま残る）・改名された・同名が増えて曖昧になった・同名が消えて曖昧が解けた。
    // 解決は起点の本文が変わったときにしか走らないので、Obsidian の初回同期のように順不同で届くと
    // 半分のリンクが恒久的に辺にならない（#1396 §調べたこと f-5）。
    //
    // 🔴 **移行前の行（構文の別が null）を 1 行でも持つ起点は作り直さない**（null を返す）。
    // 型が分からない行で作り直すと、`[[a#h]]`（cites）や `![[a]]`（embeds）の辺を related へ落とし、
    // 行の無いリンクの辺は「本文から消えた」と解釈して消してしまう。
    public async Task<SyncResult?> RebuildAsync(
        Guid sourceDocumentId, CandidateOverride? pending, CancellationToken ct = default)
    {
        var rows = await db.DocumentLinkTargets.AsNoTracking()
            .Where(t => t.SourceDocumentId == sourceDocumentId)
            .ToListAsync(ct);
        if (rows.Count == 0)
            return null;

        var restored = rows.Select(r => r.ToLink()).ToList();
        if (restored.Any(l => l is null))
        {
            logger.LogInformation(
                "Skipped relinking {DocumentId}: link targets were stored before link kinds were kept",
                sourceDocumentId);
            return null;
        }

        return await ApplyAsync(sourceDocumentId, restored.Select(l => l!).ToList(), pending, ct);
    }

    // [[IADR-0521]] (#1396): 文書 D の新規・改名・撤収・削除のとき、**D を指し得るリンクを持つ起点**の辺を作り直す。
    //
    // 起点の集合（両側から引く。規則 11）:
    //   - 増える側: 保存済みのリンク先の名前が D の新しい題名・古い題名（大小文字を無視）・`doc/<D>` のもの
    //     （相手が届いた・改名で一致した・同名が増えた／消えた）。
    //   - 減る側: D に触れる本文のリンクの辺を持つ起点（改名・撤収で外れた）。
    // D 自身は除く（D の本文の辺は D の再取り込みが作る）。戻り値は作り直した起点。
    public async Task<IReadOnlyList<Guid>> RelinkReferrersAsync(
        Guid documentId, string? previousTitle, string? currentTitle, CancellationToken ct = default)
    {
        var names = new[] { previousTitle, currentTitle }
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var wikiName = WikiDocumentPath.Format(documentId);

        var byName = await db.DocumentLinkTargets.AsNoTracking()
            .Where(t => t.SourceDocumentId != documentId
                && (t.Target == wikiName || names.Contains(t.Target.ToLower())))
            .Select(t => t.SourceDocumentId)
            .Distinct()
            .ToListAsync(ct);
        var byEdge = await db.Edges.AsNoTracking()
            .Where(e => e.Provenance == EdgeProvenance.Auto
                && e.AutoSource == EdgeAutoSource.Link
                && e.ExtractedFrom != null
                && e.ExtractedFrom != documentId
                && (e.SourceDocumentId == documentId || e.TargetDocumentId == documentId))
            .Select(e => e.ExtractedFrom!.Value)
            .Distinct()
            .ToListAsync(ct);

        var pending = new CandidateOverride(documentId, currentTitle);
        var rebuilt = new List<Guid>();
        foreach (var source in byName.Concat(byEdge).Distinct().OrderBy(id => id))
        {
            if (await RebuildAsync(source, pending, ct) is not null)
                rebuilt.Add(source);
        }
        return rebuilt;
    }

    // 抽出済みのリンク列から、当該文書を起点とする本文のリンクの辺を差分更新する。
    private async Task<SyncResult> ApplyAsync(
        Guid documentId, IReadOnlyList<ObsidianLink> links, CandidateOverride? pending, CancellationToken ct)
    {
        var types = await db.EdgeTypes.AsNoTracking().ToListAsync(ct);
        var byName = types.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(types.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        // [3] 名前 → 文書 ID。**辺を作れるリンクが 1 本も無ければ照会もしない。**
        var (resolved, unresolved) = links.Count == 0
            ? (new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase), 0)
            : await ResolveTargetsAsync(links, pending, ct);

        // 望ましい辺の集合。キーは ux_edges と同じ 5 つ組（正規化後）。
        var desired = new Dictionary<EdgeKey, Edge>();
        foreach (var link in links)
        {
            if (EdgeTypeResolver.Resolve(link, known) is not { } resolution)
            {
                // 既定型 `related` すら辞書に無い（seed 前）。**辺を作らない。**
                logger.LogWarning(
                    "Edge type dictionary has no '{DefaultType}'; link to {Target} is skipped",
                    EdgeTypeResolver.DefaultTypeName, link.Target);
                continue;
            }

            if (resolution.IsFallback)
            {
                // ADR-0033 決定 3: 未定義型は related へ丸め、**警告を記録して取り込む**。
                // 型名はログへ（カウンタのタグにすると系列が無界になる）。
                var layer = link.ExplicitTypeName is not null
                    ? EdgeTypeFallbackMetrics.ExplicitLayer
                    : EdgeTypeFallbackMetrics.ContextualLayer;
                logger.LogWarning(
                    "Unknown edge type '{RequestedType}' in document {DocumentId} ({Layer}); "
                    + "falling back to '{DefaultType}'",
                    resolution.RequestedTypeName, documentId, layer, resolution.TypeName);
                metrics.RecordFallback(layer);
            }

            if (!byName.TryGetValue(resolution.TypeName, out var type))
                continue;

            // 解決できないリンクは辺を作らない（#912 の既定。宙ぶらりんのノードを作らない）。
            if (!resolved.TryGetValue(link.Target, out var targetId))
                continue;
            // 自己参照。辺の一意制約以前に、探索で意味を持たない。
            if (targetId == documentId)
                continue;

            var edge = Edge.Create(
                documentId, targetId, type.Id, type.IsSymmetric, EdgeProvenance.Auto,
                sourceAnchor: null, targetAnchor: Truncate(link.Anchor), extractedFrom: documentId,
                autoSource: EdgeAutoSource.Link);
            // 同じ 5 つ組が本文に 2 度現れても辺は 1 本（ux_edges と同じ粒度）。
            desired.TryAdd(EdgeKey.Of(edge), edge);
        }

        // [4] 差分。**端点に当該文書を含む辺を引く**（対称型は正規化で Source/Target の
        // どちらにも来るため、片側だけを見ると取りこぼす）。
        // ［[[IADR-0521]]］同じ保存の中の未保存の追加・削除も見る（後着の作り直し・共有タグの差分と同居するため）。
        var live = (await TrackedEdges.TouchingAsync(db, documentId, ct))
            .Where(TrackedEdges.IsLive)
            .Select(e => e.Entity)
            .ToList();

        // 🔴 削除するのは「**当該文書の本文から自動抽出した**辺」だけである（ADR-0033 決定 6:
        // 利用者付与の辺と承認済み AI 提案の辺は再取り込みで消さない）。他文書起点の auto 辺も
        // 消さない —— それらは向こうの文書の本文が正本である。共有タグの辺も消さない（起点を持たない）。
        var stale = live
            .Where(e => e.Provenance == EdgeProvenance.Auto
                && !e.IsTagDerived
                && e.ExtractedFrom == documentId
                && !desired.ContainsKey(EdgeKey.Of(e)))
            .ToList();
        if (stale.Count > 0)
            db.Edges.RemoveRange(stale);

        // 追加は「**どの出所の**既存辺とも一致しないもの」に限る —— 利用者が既に張っている同じ
        // 関係へ auto の辺を重ねると ux_edges で衝突する（そして人の辺を auto で覆わない）。
        // ［[[IADR-0521]]］**共有タグの辺は引き取る**（本文のリンクが同じ関係を表明している。行は消さずに書き換える）。
        var staleSet = stale.ToHashSet();
        var current = live.Where(e => !staleSet.Contains(e)).ToList();
        var occupied = current.Where(e => !e.IsTagDerived).Select(EdgeKey.Of).ToHashSet();
        var tagDerived = current.Where(e => e.IsTagDerived)
            .GroupBy(EdgeKey.Of)
            .ToDictionary(g => g.Key, g => g.First());

        var added = new List<Edge>();
        var claimed = 0;
        foreach (var (key, edge) in desired)
        {
            if (occupied.Contains(key))
                continue;
            if (tagDerived.TryGetValue(key, out var shared))
            {
                shared.ClaimAsLink(documentId);
                claimed++;
                continue;
            }
            added.Add(edge);
        }
        if (added.Count > 0)
            db.Edges.AddRange(added);

        return new SyncResult(links.Count, added.Count, stale.Count, unresolved, claimed);
    }

    // 本文が指すリンク先の名前を**全量置換**で保存する（[[IADR-0389]] 決定 3 / #1246）。
    //
    // 🔴 **SaveChanges を呼ばない**（本クラスの他の書き込みと同じ。呼び出し元が 1 回だけ保存する）。
    // 🔴 **リンクが 0 本でも呼ぶ。** 本文からリンクを消した文書の行が残ると、
    // 未解決リンク数が恒久的に減らない（受け口のスナップショット置換と同じ理由）。
    private async Task ReplaceLinkTargetsAsync(
        Guid documentId, IReadOnlyList<ObsidianLink> links, CancellationToken ct)
    {
        var existing = await db.DocumentLinkTargets
            .Where(t => t.SourceDocumentId == documentId)
            .ToListAsync(ct);
        if (existing.Count > 0)
            db.DocumentLinkTargets.RemoveRange(existing);

        var now = DateTimeOffset.UtcNow;
        // ［[[IADR-0521]]］1 リンク 1 行。名前（ordinal）・構文の別・明示型・アンカーの組で重複を落とす。
        foreach (var link in links
                     .Where(l => l.Target.Length > 0)
                     .DistinctBy(l => (l.Target, l.Kind, l.ExplicitTypeName, l.Anchor)))
            db.DocumentLinkTargets.Add(DocumentLinkTarget.Create(documentId, link, now));
    }

    // 空でない相手の名前を重複なしで。**ordinal で重複排除する** ——
    // 大文字小文字だけが違う 2 つのリンクは、解決規則の上では別の問い合わせである。
    private static List<string> DistinctTargets(IReadOnlyList<ObsidianLink> links)
        => links
            .Select(l => l.Target)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // リンク先の名前を文書 ID へ解決する（IADR-0281）。
    //
    // **照会先は graph_documents の複製 Title である**（鮮度契約 1: 正本 DocumentService への同期
    // 照会をしない）。**判定そのものは `LinkTargetMatcher` が持つ** —— 収集側（未解決リンク数）が
    // 同じ規則で数えるためであり、ここに規則を書き戻してはならない（[[IADR-0389]] 決定 3）。
    //
    // 戻り値の `Unresolved` は不在・曖昧の合計（ログ用）。
    private async Task<(Dictionary<string, Guid> Resolved, int Unresolved)> ResolveTargetsAsync(
        IReadOnlyList<ObsidianLink> links, CandidateOverride? pending, CancellationToken ct)
    {
        var targets = DistinctTargets(links);
        var resolved = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0)
            return (resolved, 0);

        // 候補を **1 クエリ**で引く。ordinal 一致と大文字小文字を無視した一致の**両方の候補**を
        // まとめて取り、選別は `LinkTargetMatcher` に任せる
        // （PostgreSQL の既定照合順序では `=` がそのまま ordinal 比較である）。
        var lowered = targets.Select(t => t.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        // ［[[IADR-0521]]］Wiki のリンク（`doc/<ID>`）は ID で候補に入れる。
        var wikiIds = targets
            .Select(t => WikiDocumentPath.TryParse(t, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var rows = await db.Documents.AsNoTracking()
            .Where(d => targets.Contains(d.Title) || lowered.Contains(d.Title.ToLower())
                || wikiIds.Contains(d.DocumentId))
            .Select(d => new { d.DocumentId, d.Title })
            .ToListAsync(ct);
        var candidates = rows
            // ［[[IADR-0521]]］未保存の値で上書きする文書は DB の行を使わない。
            .Where(r => pending is null || r.DocumentId != pending.Value.DocumentId)
            .Select(r => new LinkTargetMatcher.TitleCandidate(r.DocumentId, r.Title))
            .ToList();
        if (pending is { Title: { } pendingTitle } p)
            candidates.Add(new LinkTargetMatcher.TitleCandidate(p.DocumentId, pendingTitle));

        var unresolved = 0;
        foreach (var target in targets)
        {
            var match = LinkTargetMatcher.Match(target, candidates);
            if (match.IsResolved)
            {
                resolved[target] = match.DocumentId;
                continue;
            }

            unresolved++;
            if (match.Outcome == LinkTargetMatcher.LinkTargetOutcome.Ambiguous)
                logger.LogWarning("Ambiguous link target '{Target}'", target);
            else
                logger.LogInformation("Unresolved link target '{Target}'; no edge is created", target);
        }

        return (resolved, unresolved);
    }

    private static string? Truncate(string? anchor)
        => anchor is { Length: > MaxAnchorLength } ? anchor[..MaxAnchorLength] : anchor;
}
