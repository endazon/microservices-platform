using DocumentService.Domain;
using DocumentService.Features.SyncConflicts;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.PrivateNotes;

// FR-19, FR-20, SC-19 主要素 1・2・5, ADR-0036 D-06, ADR-0037 決定 3・4・7, ADR-0063, #1441:
// 個人資料 DTO の**導出項目**（公開範囲・共有件数・同期状態・タグ名）を作る材料を、
// **所有者ごとに 1 回だけ**引いて畳む。
//
// 🔴 **導出を写像（`PrivateNoteMapper`）へ持ち込まない。** 生成マッパは列の詰め替えだけを行い、
// 「共有台帳に group 行があるか」「未解決の競合があるか」は写像の材料ではなく判断である
// （IADR-0406 決定 1 と同じ切り分け。時計・DB 照会は端で畳む）。
//
// 🔴 **N+1 を作らない。** 資料 1 件ごとに共有・競合・端末・設定を引くと、一覧（削除済みを含む
// 全件）の応答が資料数に比例して遅くなる。ここで引くのは**所有者あたり 5 回**（共有・競合・端末・
// 同期設定・タグ辞書）で固定である。
//
// **4 操作が使う**（一覧・作成・復元・露出更新）ため置き場は 2 段目である（ADR-0068 決定 2。
// `PrivateNoteMapper` と同じ理由・同じ段）。
internal sealed class PrivateNoteEnrichment
{
    private readonly Dictionary<Guid, int> _userShares;
    private readonly Dictionary<Guid, int> _groupShares;
    private readonly HashSet<Guid> _unresolvedConflicts;
    private readonly bool _hasActiveDevice;
    private readonly IReadOnlyList<string> _targetFolders;
    private readonly IReadOnlyDictionary<Guid, string> _tagNames;

    private PrivateNoteEnrichment(Dictionary<Guid, int> userShares,
        Dictionary<Guid, int> groupShares, HashSet<Guid> unresolvedConflicts,
        bool hasActiveDevice, IReadOnlyList<string> targetFolders,
        IReadOnlyDictionary<Guid, string> tagNames)
    {
        _userShares = userShares;
        _groupShares = groupShares;
        _unresolvedConflicts = unresolvedConflicts;
        _hasActiveDevice = hasActiveDevice;
        _targetFolders = targetFolders;
        _tagNames = tagNames;
    }

    // #1441: 導出の材料をまとめて引く。`noteIds` は写像する資料の集合（共有と競合はこれで絞る）。
    internal static async Task<PrivateNoteEnrichment> LoadAsync(DocumentDbContext db, string owner,
        IReadOnlyCollection<Guid> noteIds, DateTimeOffset now, CancellationToken ct)
    {
        // ADR-0036 D-06: 公開範囲は共有台帳の行から導く（属性辞書には載っていない）。
        var shares = noteIds.Count == 0
            ? []
            : await db.DocumentShares.Where(s => noteIds.Contains(s.DocumentId))
                .Select(s => new { s.DocumentId, s.SubjectType }).ToListAsync(ct);

        var userShares = shares.Where(s => s.SubjectType == ShareSubjectType.User)
            .GroupBy(s => s.DocumentId).ToDictionary(g => g.Key, g => g.Count());
        var groupShares = shares.Where(s => s.SubjectType == ShareSubjectType.Group)
            .GroupBy(s => s.DocumentId).ToDictionary(g => g.Key, g => g.Count());

        // ADR-0037 決定 7: 未解決の競合は同期状態の最優先の材料である。
        // 🔴 **述語は `SyncConflictEndpoints.Unresolved` の 1 本**（一覧・成功 push の閉じ込みと同じ）——
        // ここへ書き下すと「一覧には出るのに `syncState` が `conflict` にならない」がいずれ起きる。
        var conflicts = await SyncConflictEndpoints.Unresolved(db, owner)
            .Select(c => c.DocumentId).Distinct().ToListAsync(ct);

        // ADR-0037 決定 3: 同期対象の前提は「有効な同期端末が 1 台以上ある」こと。
        // 🔴 **述語は `SyncDevice.IsActive` を使う**（未失効かつ期限内。ここへ書き下すと 2 本になる）。
        var devices = await db.SyncDevices.Where(d => d.OwnerId == owner).ToListAsync(ct);

        // ADR-0037 決定 3・4: 同期対象フォルダ（未設定なら全資料が対象）。
        var settings = await db.SyncSettings.FindAsync([owner], ct);

        return new PrivateNoteEnrichment(userShares, groupShares, [.. conflicts],
            devices.Any(d => d.IsActive(now)),
            settings?.TargetFolders ?? [],
            await TagResolver.NamesAsync(db, ct));
    }

    // 🔴 **材料が 1 つも無い状態**（共有 0 行・未解決の競合なし・有効端末なし・フォルダ未設定・
    // タグ辞書が空）。導出は `private` / `excluded` / `[]` になる。
    // **端に残した縮退（題・版）だけを測る単体試験のための口であり、端点からは使わない** ——
    // 端点が使うと、材料を引かないまま全資料が「非公開・対象外」に化ける。
    internal static PrivateNoteEnrichment Empty { get; } =
        new([], [], [], hasActiveDevice: false, [], new Dictionary<Guid, string>());

    // FR-19, SC-19: 台帳 1 行 → 応答 DTO（導出項目つき）。
    internal PrivateNoteDto ToDto(PrivateNote n, Document? doc)
    {
        var users = _userShares.GetValueOrDefault(n.DocumentId);
        var groups = _groupShares.GetValueOrDefault(n.DocumentId);
        return PrivateNoteMapper.ToDto(n, doc?.Title ?? string.Empty, doc?.Version ?? 0,
            VisibilityOf(users, groups), users, groups, SyncStateOf(n),
            TagResolver.ToNames(doc?.Tags ?? [], _tagNames));
    }

    // SC-19 主要素 2, ADR-0036 D-06: 公開範囲の 3 状態。
    // **グループ共有は個人共有より広い**ため、両方あるときは `groups` を採る。
    internal static string VisibilityOf(int sharedUserCount, int sharedGroupCount)
        => sharedGroupCount > 0 ? PrivateNoteVisibilityValues.Groups
        : sharedUserCount > 0 ? PrivateNoteVisibilityValues.Users
        : PrivateNoteVisibilityValues.Private;

    // SC-19 主要素 5, ADR-0037 決定 3・4・7: 同期状態の 3 状態。
    // 🔴 **競合が最優先である** —— 利用者が最初に気付くべき状態であり、対象／対象外より強い。
    private string SyncStateOf(PrivateNote n)
    {
        if (_unresolvedConflicts.Contains(n.DocumentId)) return PrivateNoteSyncStates.Conflict;
        // 削除済みの資料は同期の対象にならない（Obsidian 側からは消えている）。
        if (n.IsDeleted) return PrivateNoteSyncStates.Excluded;
        if (!_hasActiveDevice) return PrivateNoteSyncStates.Excluded;
        return IsUnderTargetFolders(n.VaultPath, _targetFolders)
            ? PrivateNoteSyncStates.Target
            : PrivateNoteSyncStates.Excluded;
    }

    // ADR-0037 決定 3・4: **フォルダ未設定は「全資料が対象」**（空集合＝対象外ではない）。
    // 配下の判定は `<フォルダ>/` の前方一致である —— `notes` が `notes-old/a.md` を拾わない。
    internal static bool IsUnderTargetFolders(string vaultPath, IReadOnlyList<string> folders)
        => folders.Count == 0
        || folders.Any(f => vaultPath.StartsWith(f + "/", StringComparison.Ordinal));
}
