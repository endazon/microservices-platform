using GraphService.Domain;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Infrastructure.Persistence;

// FR-17, SC-09, ADR-0033 決定 3: 辺の型の初期値集合。
//
// **中核 5 種を含む 3 層構成**で、自動抽出の既定型は `related`（決定 3）。
// **これはコード定義ではなく初期データである** —— 以後の追加・改名・削除は SC-09 から行い
// （#910）、コードは触らない。seed はあくまで「空の辞書で始めない」ためのものである。
public static class EdgeTypeSeed
{
    // ADR-0033 決定 3: 自動抽出の既定型。未定義型のフォールバック先でもある（#912）。
    public const string DefaultTypeName = "related";

    // (name, layer, isSymmetric, weight)
    //
    // FR-04, ADR-0035 決定 2 (#947a): **重みは二段検索の再ランクが使う**（#970 で配線済み。
    // `/graph/edge-types/catalog` が公開し、RetrievalService が引く）。
    // ADR が名指しで決めているのは 2 つだけである ——「`supersedes` は最新版へ**強く**誘導し、
    // `related` は**弱く**扱う」。**残りは実装側の判断**であり、根拠を仕様書へ書いた。
    //
    // 🔴 **これは実測値ではない。** 実データの次数・型分布が無い（取り込み経路が未配線）。
    // 実データが入った時点で測り直す。
    private static readonly (string Name, string Layer, bool IsSymmetric, double Weight)[] Entries =
    [
        // 中核 5 種。related のみ対称。
        (DefaultTypeName, EdgeTypeLayer.Core,        true,  0.3), // ADR: 弱く扱う
        ("cites",         EdgeTypeLayer.Core,        false, 0.7),
        ("supersedes",    EdgeTypeLayer.Core,        false, 1.0), // ADR: 強く誘導する
        ("derived-from",  EdgeTypeLayer.Core,        false, 0.8), // 系列辿りの主軸
        ("embeds",        EdgeTypeLayer.Core,        false, 0.7),
        // 追加推奨 4 種。差を付ける根拠が無いので中庸で揃える。
        ("implements",    EdgeTypeLayer.Recommended, false, EdgeType.DefaultWeight),
        ("refines",       EdgeTypeLayer.Recommended, false, EdgeType.DefaultWeight),
        ("depends-on",    EdgeTypeLayer.Recommended, false, EdgeType.DefaultWeight),
        ("part-of",       EdgeTypeLayer.Recommended, false, EdgeType.DefaultWeight),
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
            .Select(e => EdgeType.Create(e.Name, e.Layer, e.IsSymmetric, isSeed: true, weight: e.Weight))
            .ToList();

        if (missing.Count == 0)
            return;

        db.EdgeTypes.AddRange(missing);
        await db.SaveChangesAsync(ct);
    }
}
