namespace DocumentService.Domain;

// FR-20, UC-11, SC-20 主要素 5, ADR-0037 決定 7, [[IADR-0352]], #1442: 同期の競合 1 件。
//
// **サーバは自動解決しない**（決定 7）。push の `baseVersion` が現在版と食い違ったとき、
// 409 を返す**前に**この行を起こし、利用者が SC-20 で 3 択（ローカル採用／サーバ採用／両方残す）を
// 選ぶまで未解決のまま残す。🔴 **プロトコルの 409 応答（本文・状態）は変えない** ——
// プラグインは従来どおり自分で解決でき、通れば本行は `client` として閉じる。
//
// **ローカル本文は行に持たない。** オブジェクトストレージへ置き、参照だけを持つ
// （本文を DB へ入れると台帳が本文を持つことになり、`PrivateNote` が本文を持たない設計と食い違う）。
public class SyncConflict
{
    // #1442: ローカル本文の格納鍵（`private-notes/conflicts/{conflictId}`）。
    // **鍵は競合 ID で固定である** —— 同じ資料・同じ端末の未解決競合を上書きするとき、
    // 行を増やさず本文だけを最新へ置き換えられる。
    public static string StorageKey(Guid conflictId) => $"private-notes/conflicts/{conflictId:D}";

    public Guid Id { get; private set; } = Guid.NewGuid();

    // 競合した個人資料（`PrivateNote.DocumentId`）。資料の完全削除で連動削除する。
    public Guid DocumentId { get; private set; }
    public string OwnerId { get; private set; } = string.Empty;

    // FR-20: 検出したときの端末（SC-20 の一覧が「どの端末の編集か」を示す）。
    public Guid DeviceId { get; private set; }

    // 端末が土台にしていた版と、検出時のサーバの版。2 ペイン差分の見出しになる。
    public int LocalBaseVersion { get; private set; }
    public int ServerVersion { get; private set; }

    // ローカル本文の参照 URI（オブジェクトストレージ）。解決時に消す。
    public string LocalContentUri { get; private set; } = string.Empty;

    public DateTimeOffset DetectedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    // `SyncConflictResolutions` の値（`local` / `server` / `both` / `client`）。未解決なら null。
    public string? Resolution { get; private set; }

    private SyncConflict() { }

    public static SyncConflict Detect(Guid documentId, string ownerId, Guid deviceId,
        int localBaseVersion, int serverVersion, DateTimeOffset now) => new()
        {
            DocumentId = documentId,
            OwnerId = ownerId,
            DeviceId = deviceId,
            LocalBaseVersion = localBaseVersion,
            ServerVersion = serverVersion,
            DetectedAt = now,
        };

    public bool IsResolved => ResolvedAt is not null;

    // ADR-0037 決定 7: 同じ資料・同じ端末の未解決競合は**上書きする**（行を増やさない）。
    // 端末がオフラインで何度も push を試すたびに行が増えると、SC-20 の一覧が同じ競合で埋まる。
    public void Redetect(int localBaseVersion, int serverVersion, DateTimeOffset now)
    {
        LocalBaseVersion = localBaseVersion;
        ServerVersion = serverVersion;
        DetectedAt = now;
    }

    public void RecordLocalContent(string uri) => LocalContentUri = uri;

    // SC-20 主要素 5: 解決の記録。**冪等ではない** —— 解決済みへの再解決は端点が 409 で止める
    // （既に版を進めた後にもう一度進めてしまうのを防ぐ）。
    public void Resolve(string resolution, DateTimeOffset now)
    {
        Resolution = resolution;
        ResolvedAt = now;
    }
}
