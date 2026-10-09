using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GraphService.Features.GraphDocuments.Sync;

// FR-17, UC-10, ADR-0033 決定 3・4・6, ADR-0035 決定 1, [[IADR-0522]] (#1396):
// **同じタグを持つ文書の組を辺で結ぶ**（利用者裁定 2026-10-09: 辺は「文書内の明示リンク」と
// 「同じタグを持つ文書」で結ぶ。同じフォルダ・埋め込みの類似度では結ばない）。
//
// ## 形
//
// - 型は既定型 `related`（対称。ADR-0033 決定 3 の自動抽出の既定）・出所は `auto`・内訳 `AutoSource=tag`。
// - **組あたり 1 本**（共有するタグが 2 つでも 1 本）。どのタグで結ばれたかは辺に持たない。
// - **ハブ**（文書数が上限を超えるタグ）からは作らない（`TagEdgeRule`）。タグノードは作らない
//   （計画のノードは文書だけである）。
//
// ## 差分の範囲
//
// 受信した文書 D の組だけを作り直す。**ただしタグの文書数が上限を跨いだ**（D が入ってハブになった・
// 抜けてハブでなくなった）ときは、そのタグの所属文書の組も作り直す —— 跨いだ瞬間に、D を含まない組
// （A–B）の要否も変わるためである。上限を跨がない限り、D を含まない組は D のタグの変化で変わらない。
//
// ## 他の辺との関係
//
// - 同じ 5 つ組の辺が既にあれば重ねない（本文のリンク・利用者付与・AI 承認のどれでも）。
// - 本文のリンクの辺が同じ保存で消された組がタグを共有していれば、**その行を共有タグの辺へ戻す**
//   （消して入れ直すと一意索引 `ux_edges` に当たる。`Edge.ConvertToTagDerived`）。
//
// 🔴 **SaveChanges を呼ばない**（`LinkEdgeSynchronizer` と同じ。呼び出し元が 1 回だけ保存する）。
public sealed class TagEdgeSynchronizer(
    GraphDbContext db,
    IOptions<TagEdgeOptions> options,
    ILogger<TagEdgeSynchronizer> logger)
{
    public readonly record struct SyncResult(int Tags, int Added, int Removed, int HubTags, int Reconciled);

    // 文書 D のタグを `tags` に置き換え、共有タグの辺を作り直す。
    // `alsoReconcile` は、同じ保存の中で本文のリンクの辺を作り直した他の文書（その組のタグの辺を戻すため）。
    public async Task<SyncResult> SyncAsync(
        Guid documentId,
        IEnumerable<string>? tags,
        IReadOnlyCollection<Guid>? alsoReconcile = null,
        CancellationToken ct = default)
    {
        var max = options.Value.EffectiveMaxDocumentsPerTag;
        var next = GraphDocumentTag.Normalize(tags);

        // [1] D のタグの複製を全量置換する（差分で足し引き。主キーが 2 列なので消して入れ直さない）。
        var rows = await db.DocumentTags.Where(t => t.DocumentId == documentId).ToListAsync(ct);
        var previous = rows.Select(r => r.Tag).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => !next.Contains(r.Tag)))
            db.DocumentTags.Remove(row);
        foreach (var tag in next.Where(t => !previous.Contains(t)))
            db.DocumentTags.Add(GraphDocumentTag.Create(documentId, tag));

        var view = new TagView(db, documentId, next);
        var affected = previous.Union(next, StringComparer.Ordinal).ToList();
        await view.LoadCountsAsync(affected, ct);

        // [2] 上限を跨いだタグ。前後の件数は「D 以外の件数 ＋ D が持っていたか／持つか」。
        var toReconcile = new HashSet<Guid> { documentId };
        foreach (var tag in affected)
        {
            var others = view.CountExcludingSelf(tag);
            var before = others + (previous.Contains(tag) ? 1 : 0);
            var after = others + (next.Contains(tag) ? 1 : 0);
            if (TagEdgeRule.IsHub(before, max) == TagEdgeRule.IsHub(after, max))
                continue;
            foreach (var member in await view.MembersAsync(tag, ct))
                toReconcile.Add(member);
        }
        if (alsoReconcile is not null)
            toReconcile.UnionWith(alsoReconcile);

        // [3] 既定型。本文のリンクと同じく**名前で**引く（`LinkEdgeSynchronizer` / `EdgeTypeResolver`）。
        var related = (await db.EdgeTypes.AsNoTracking().ToListAsync(ct))
            .FirstOrDefault(t => string.Equals(
                t.Name, EdgeTypeResolver.DefaultTypeName, StringComparison.OrdinalIgnoreCase));
        if (related is null)
        {
            // 既定型 `related` が辞書に無い（seed 前・改名された）。**辺を触らない**（タグの複製だけ進める）。
            logger.LogWarning(
                "Edge type dictionary has no '{DefaultType}'; tag edges of {DocumentId} are left as they are",
                EdgeTypeResolver.DefaultTypeName, documentId);
            return new SyncResult(next.Count, 0, 0, 0, 0);
        }

        var added = 0;
        var removed = 0;
        // 決定的な順序で処理する（試験の再現性。結果は順序に依らない）。
        foreach (var member in toReconcile.OrderBy(id => id))
        {
            var (a, r) = await ReconcileAsync(member, view, related, max, ct);
            added += a;
            removed += r;
        }

        var hubTags = next.Count(t => TagEdgeRule.IsHub(view.Count(t), max));
        return new SyncResult(next.Count, added, removed, hubTags, toReconcile.Count);
    }

    // 文書 M に触れる共有タグの辺を、M の今のタグから決まる相手の集合へ揃える。
    private async Task<(int Added, int Removed)> ReconcileAsync(
        Guid member, TagView view, EdgeType related, int max, CancellationToken ct)
    {
        var tags = await view.TagsOfAsync(member, ct);
        await view.LoadCountsAsync(tags, ct);
        // ハブでないタグの所属だけを読む（ハブのタグは件数で先に外す —— 所属は上限を超えて多い）。
        foreach (var tag in tags.Where(t => !TagEdgeRule.IsHub(view.Count(t), max)))
            await view.MembersAsync(tag, ct);

        var partners = TagEdgeRule.DesiredPartners(member, tags, view.Count, view.CachedMembers, max);
        var desired = new Dictionary<EdgeKey, Guid>();
        foreach (var partner in partners)
        {
            var probe = Edge.Create(member, partner, related.Id, related.IsSymmetric, EdgeProvenance.Auto,
                autoSource: EdgeAutoSource.Tag);
            desired.TryAdd(EdgeKey.Of(probe), partner);
        }

        var entries = await TrackedEdges.TouchingAsync(db, member, ct);
        var live = entries.Where(TrackedEdges.IsLive).ToList();

        var removed = 0;
        foreach (var entry in live.Where(e => e.Entity.IsTagDerived && !desired.ContainsKey(EdgeKey.Of(e.Entity))))
        {
            db.Edges.Remove(entry.Entity);
            removed++;
        }

        var occupied = live
            .Where(e => !(e.Entity.IsTagDerived && !desired.ContainsKey(EdgeKey.Of(e.Entity))))
            .Select(e => EdgeKey.Of(e.Entity))
            .ToHashSet();
        var deleted = entries
            .Where(e => e.State == EntityState.Deleted && e.Entity.Provenance == EdgeProvenance.Auto)
            .GroupBy(e => EdgeKey.Of(e.Entity))
            .ToDictionary(g => g.Key, g => g.First());

        var added = 0;
        foreach (var (key, partner) in desired)
        {
            if (occupied.Contains(key))
                continue;

            if (deleted.TryGetValue(key, out var gone))
            {
                // 同じ保存で消される自動抽出の辺（本文のリンクが消えた）を、共有タグの辺として残す。
                gone.Entity.ConvertToTagDerived();
                gone.State = EntityState.Modified;
            }
            else
            {
                db.Edges.Add(Edge.Create(member, partner, related.Id, related.IsSymmetric, EdgeProvenance.Auto,
                    autoSource: EdgeAutoSource.Tag));
            }
            occupied.Add(key);
            added++;
        }

        return (added, removed);
    }

    // タグ → 件数・所属、文書 → タグ の読み込みと保持。**D（受信した文書）の行だけは未保存の新しい値で見る**
    // （DB への問い合わせは D の行を除外し、`next` で補う）。
    private sealed class TagView(GraphDbContext db, Guid self, HashSet<string> selfTags)
    {
        private readonly Dictionary<string, int> _countsExcludingSelf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Guid>> _members = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, IReadOnlyCollection<string>> _tagsOf = [];

        public async Task LoadCountsAsync(IEnumerable<string> tags, CancellationToken ct)
        {
            var missing = tags.Where(t => !_countsExcludingSelf.ContainsKey(t)).Distinct().ToList();
            if (missing.Count == 0) return;
            var counts = await db.DocumentTags.AsNoTracking()
                .Where(r => missing.Contains(r.Tag) && r.DocumentId != self)
                .GroupBy(r => r.Tag)
                .Select(g => new { Tag = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            foreach (var tag in missing)
                _countsExcludingSelf[tag] = 0;
            foreach (var c in counts)
                _countsExcludingSelf[c.Tag] = c.Count;
        }

        public int CountExcludingSelf(string tag) => _countsExcludingSelf.GetValueOrDefault(tag);

        public int Count(string tag) => CountExcludingSelf(tag) + (selfTags.Contains(tag) ? 1 : 0);

        public async Task<IReadOnlyCollection<Guid>> MembersAsync(string tag, CancellationToken ct)
        {
            if (_members.TryGetValue(tag, out var cached)) return cached;
            var members = await db.DocumentTags.AsNoTracking()
                .Where(r => r.Tag == tag && r.DocumentId != self)
                .Select(r => r.DocumentId)
                .ToListAsync(ct);
            if (selfTags.Contains(tag)) members.Add(self);
            _members[tag] = members;
            return members;
        }

        public IReadOnlyCollection<Guid> CachedMembers(string tag)
            => _members.TryGetValue(tag, out var members) ? members : [];

        public async Task<IReadOnlyCollection<string>> TagsOfAsync(Guid documentId, CancellationToken ct)
        {
            if (documentId == self) return selfTags;
            if (_tagsOf.TryGetValue(documentId, out var cached)) return cached;
            var tags = await db.DocumentTags.AsNoTracking()
                .Where(r => r.DocumentId == documentId)
                .Select(r => r.Tag)
                .ToListAsync(ct);
            _tagsOf[documentId] = tags;
            return tags;
        }
    }
}
