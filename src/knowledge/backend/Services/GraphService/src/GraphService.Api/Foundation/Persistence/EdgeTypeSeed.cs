using GraphService.Api.Foundation.Domain;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Api.Foundation.Persistence;

// FR-17, SC-09, ADR-0033 決定 3: 辺の型の初期値集合。
//
// **中核 5 種を含む 3 層構成**で、自動抽出の既定型は `related`（決定 3）。
// **これはコード定義ではなく初期データである** —— 以後の追加・改名・削除は SC-09 から行い
// （#910）、コードは触らない。seed はあくまで「空の辞書で始めない」ためのものである。
public static class EdgeTypeSeed
{
    // ADR-0033 決定 3: 自動抽出の既定型。未定義型のフォールバック先でもある（#912）。
    public const string DefaultTypeName = "related";

    // (name, layer, isSymmetric)
    private static readonly (string Name, string Layer, bool IsSymmetric)[] Entries =
    [
        // 中核 5 種。related のみ対称。
        (DefaultTypeName, EdgeTypeLayer.Core,        true),
        ("cites",         EdgeTypeLayer.Core,        false),
        ("supersedes",    EdgeTypeLayer.Core,        false),
        ("derived-from",  EdgeTypeLayer.Core,        false),
        ("embeds",        EdgeTypeLayer.Core,        false),
        // 追加推奨 4 種。
        ("implements",    EdgeTypeLayer.Recommended, false),
        ("refines",       EdgeTypeLayer.Recommended, false),
        ("depends-on",    EdgeTypeLayer.Recommended, false),
        ("part-of",       EdgeTypeLayer.Recommended, false),
    ];

    public static int Count => Entries.Length;

    // 起動時に不足分だけを入れる（冪等）。
    //
    // **既存の型は一切触らない。** 改名済みの型を seed の名前へ戻すと、SC-09 での改名
    // （ADR-0033 決定 9）が起動のたびに巻き戻ることになる。判定は Id ではなく名前で行い、
    // 「その名前が無ければ足す」だけにする。
    public static async Task EnsureSeededAsync(GraphDbContext db, CancellationToken ct = default)
    {
        var existing = await db.EdgeTypes
            .Select(t => t.Name)
            .ToListAsync(ct);
        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var missing = Entries
            .Where(e => !have.Contains(e.Name))
            .Select(e => EdgeType.Create(e.Name, e.Layer, e.IsSymmetric, isSeed: true))
            .ToList();

        if (missing.Count == 0)
            return;

        db.EdgeTypes.AddRange(missing);
        await db.SaveChangesAsync(ct);
    }
}
