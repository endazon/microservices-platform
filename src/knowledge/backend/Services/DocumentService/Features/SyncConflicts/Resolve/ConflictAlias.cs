namespace DocumentService.Features.SyncConflicts.Resolve;

// FR-20, SC-20 主要素 5, ADR-0037 決定 7, [[IADR-0444]], #1442:
// 「両方を残す」（`both`）で作る別名資料の名前。
//
// 計画は「別名保存」とだけ定めており、**名前の形は実装判断**である（作業仕様書 §計画書との差異）。
// 拡張子の**前**へ入れるのは、Obsidian が `.md` でしか資料を開かないためである
// （末尾へ付けると `memo.md (競合 …)` になり、Vault で開けないファイルができる）。
//
// **置き場は 3 段目である** —— 使うのは解決の 1 操作だけである（ADR-0068 決定 2）。
internal static class ConflictAlias
{
    // 既定の拡張子。Vault 内の資料は Markdown である（`PrivateNoteEndpoints` の作成既定と同じ）。
    internal const string DefaultExtension = ".md";

    // `notes/memo.md` ＋ 2026-09-12 10:30 → `notes/memo (競合 2026-09-12 1030).md`
    internal static string PathOf(string vaultPath, DateTimeOffset now)
    {
        var extension = Path.GetExtension(vaultPath);
        var stem = extension.Length > 0 ? vaultPath[..^extension.Length] : vaultPath;
        return $"{stem}{Suffix(now)}{(extension.Length > 0 ? extension : DefaultExtension)}";
    }

    // 題も同じ形にする（一覧で「どちらが競合の写しか」が名前だけで判るようにする）。
    internal static string TitleOf(string title, DateTimeOffset now) => $"{title}{Suffix(now)}";

    private static string Suffix(DateTimeOffset now) => $" (競合 {now:yyyy-MM-dd HHmm})";
}
