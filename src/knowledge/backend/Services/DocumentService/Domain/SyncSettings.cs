namespace DocumentService.Domain;

// FR-20, UC-11, SC-20 主要素 3, ADR-0037 決定 3・4, #1442: 利用者ごとの同期設定（同期対象範囲）。
//
// **対象フォルダが空なら全資料が対象である**（決定 3 の既定）。空集合を「何も同期しない」と
// 読み替えない —— 既定で同期が止まると、利用者は設定したつもりのない停止に気付けない。
//
// 🔴 **対象から外しても資料は削除しない**（決定 4）。ここに持つのは「同期の対象範囲」だけであり、
// 資料の生存期間（論理削除・90 日・完全削除）は `PrivateNote` が持つ。**2 つを混ぜない。**
public class SyncSettings
{
    // SC-20 主要素 3: 指定できるフォルダの上限と 1 件あたりの長さ。
    // **長さは `PrivateNote.VaultPath` の上限（1024）と揃える** —— フォルダの方が長く書けると、
    // どの資料も配下に入らない指定を保存できてしまう。
    public const int MaxTargetFolders = 100;
    public const int MaxFolderPathLength = 1024;

    public string OwnerId { get; private set; } = string.Empty;

    // ADR-0037 決定 4: Vault 内の相対パス（正規化済み。先頭・末尾に `/` を持たない）。
    public List<string> TargetFolders { get; private set; } = [];

    public DateTimeOffset UpdatedAt { get; private set; }

    private SyncSettings() { }

    public static SyncSettings Create(string ownerId, DateTimeOffset now) => new()
    {
        OwnerId = ownerId,
        UpdatedAt = now,
    };

    // SC-20 主要素 3: 対象フォルダの**置き換え**（差分適用ではない）。
    // 画面は全量を送る（「外す」は送らないことで表す）。
    public void Replace(List<string> targetFolders, DateTimeOffset now)
    {
        TargetFolders = targetFolders;
        UpdatedAt = now;
    }

    // ADR-0037 決定 4: 先頭・末尾の `/` と前後の空白を落とす。
    // **配下の判定（`<フォルダ>/` の前方一致）はこの形を前提にしている** ——
    // 正規化を通さない値が入ると、`/notes` と `notes` が別のフォルダとして重複できてしまう。
    public static string NormalizeFolder(string? path) => (path ?? string.Empty).Trim().Trim('/');
}
