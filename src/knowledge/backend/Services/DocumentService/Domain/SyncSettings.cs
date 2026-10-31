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

    // FR-19, SC-20 主要素 8, 計画 ADR-0143 決定 1・2 (#2120): AI 入力の**利用者単位の既定**。
    // 新しく作る個人資料（SC-19 の作成・同期の新規 push）の AI 入力の**初期値**であり、作成後は資料単位の値だけが効く。
    // 🔴 **変えても既存の資料は変わらない**（決定 2）。未設定と OFF は区別しない（既定 false）。
    // 🔴 横断検索・ナレッジグラフの既定は置かない（決定 4）。
    public bool AiInputDefault { get; private set; }

    // FR-20, SC-20 主要素 7, 計画 ADR-0145 決定 1, [[IADR-0667]] (#2378): **利用者単位の一時停止**。
    // `PausedAt` は今の停止期間の始まり（null＝有効）、`PausePeriodId` は停止期間の識別子（同期履歴の 1 行を引く鍵。決定 3）。
    // 🔴 **同期トークン・端末・有効期限には触れない**（失効・同期停止〔ADR-0037 決定 4〕とは別の操作）。
    public DateTimeOffset? PausedAt { get; private set; }
    public Guid? PausePeriodId { get; private set; }

    public bool IsPaused => PausedAt is not null;

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

    // FR-19, ADR-0143 決定 1・2 (#2120): AI 入力の利用者単位の既定を変える。既存の資料には触れない（呼び出し側も触れない）。
    public void SetAiInputDefault(bool value, DateTimeOffset now)
    {
        AiInputDefault = value;
        UpdatedAt = now;
    }

    // ADR-0145 決定 1: 一時停止。**停止中の一時停止は冪等**で、停止期間（始まりと識別子）を変えない ——
    // 変えると、同じ停止の拒否が同期履歴の 2 行に分かれる（決定 3）。
    public void Pause(DateTimeOffset now)
    {
        if (IsPaused) return;
        PausedAt = now;
        PausePeriodId = Guid.NewGuid();
        UpdatedAt = now;
    }

    // ADR-0145 決定 1: 再開。有効なときの再開は何もしない（冪等）。次の一時停止は新しい停止期間になる。
    public void Resume(DateTimeOffset now)
    {
        if (!IsPaused) return;
        PausedAt = null;
        PausePeriodId = null;
        UpdatedAt = now;
    }

    // ADR-0037 決定 4: 先頭・末尾の `/` と前後の空白を落とす。
    // **配下の判定（`<フォルダ>/` の前方一致）はこの形を前提にしている** ——
    // 正規化を通さない値が入ると、`/notes` と `notes` が別のフォルダとして重複できてしまう。
    public static string NormalizeFolder(string? path) => (path ?? string.Empty).Trim().Trim('/');
}
