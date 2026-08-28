using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Infrastructure.Persistence;

// FR-06, FR-09, SC-05, SC-09, #635: 文書の**識別子**と辞書の**表示名**を相互に写す（[[IADR-0153]] 決定 2）。
//
// **正本（`Document.Tags`）は識別子、外へ出す形（DTO・イベント）は表示名である。**
// **変換点をここ 1 つに閉じる**——散らすと「片方だけ識別子のまま漏れる」型の事故が起きる。
//
// **辞書は人が管理する規模**なので、1 回の照会で丸ごと読んでよい（N+1 にしない）。
public static class TagResolver
{
    // 識別子 → 表示名。**辞書から消えた識別子は落とす**——#635 の削除は使用件数 0 件のときだけ許すので、
    // 通常は起こらない。起きるとすれば手作業の DB 操作であり、**古い識別子を画面へ出すより落とすほうがよい**。
    public static async Task<Dictionary<Guid, string>> NamesAsync(
        DocumentDbContext db, CancellationToken ct = default) =>
        await db.Tags.ToDictionaryAsync(t => t.Id, t => t.Name, ct);

    public static List<string> ToNames(List<Guid> ids, IReadOnlyDictionary<Guid, string> names) =>
        [.. ids.Select(id => names.TryGetValue(id, out var n) ? n : null).OfType<string>()];

    // 表示名 → 識別子。**辞書に無い名前は `unknown` へ返し、呼び出し側が 400 にする。**
    //
    // **黙って落とさない**——SC-05 は「既定タグ辞書に整合」を求めており、
    // **画面からの手入力は自動登録しない**（計画確定・2026-08-09。[[IADR-0153]] 決定 5）。
    // 落とすと「保存できたのにタグが付いていない」という、利用者から見て説明のつかない結果になる。
    //
    // **識別子化した以上、辞書に無い名前は物理的に保存できない**——この検査は形式ではなく必然である。
    public static async Task<(List<Guid> Ids, List<string> Unknown)> ToIdsAsync(
        DocumentDbContext db, List<string>? names, CancellationToken ct = default)
    {
        if (names is null || names.Count == 0) return ([], []);

        var wanted = names
            .Select(Tag.Normalize)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var found = await db.Tags
            .Where(t => wanted.Contains(t.Name))
            .ToDictionaryAsync(t => t.Name, t => t.Id, ct);

        var ids = new List<Guid>();
        var unknown = new List<string>();
        foreach (var name in wanted)
        {
            if (found.TryGetValue(name, out var id)) ids.Add(id);
            else unknown.Add(name);
        }
        return (ids, unknown);
    }
}
