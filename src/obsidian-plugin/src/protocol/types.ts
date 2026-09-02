// FR-20, UC-11, ADR-0037 決定 2・14, [[IADR-0270]] 決定 3・7, [[IADR-0338]] 決定 4:
// DocumentService の同期プロトコル（/private-notes/sync/*）の契約の写し。
//
// 🔴 契約の正はサーバ（`Features/ObsidianSync/{Manifest,Pull}/Query.cs`）と
// `docs/api/FR-20_obsidian-sync.md` であり、**ここを変えてサーバを動かさない**（#1098 補足・制約）。
// JSON のキーは ASP.NET の既定（camelCase）。

/** manifest の 1 行。`deleted=true` の資料も現れる（サーバ側削除をプラグインが検知するため）。 */
export interface SyncManifestEntry {
  noteId: string;
  title: string;
  vaultPath: string;
  version: number;
  contentHash: string | null;
  deleted: boolean;
  updatedAt: string;
}

/** pull の応答。本文をそのまま運ぶ（個人資料が端末へ出る egress の実行点）。 */
export interface PullNoteResponse {
  noteId: string;
  title: string;
  vaultPath: string;
  version: number;
  contentHash: string | null;
  deleted: boolean;
  content: string;
}

/**
 * 401。欠落・不正・期限切れ・失効を**サーバは区別しない**（[[IADR-0270]] 決定 3）ので、
 * プラグインも区別せず「トークンが無効」として利用者に伝える。
 */
export class SyncAuthError extends Error {
  constructor() {
    super('同期トークンが受け付けられませんでした（未設定・不正・期限切れ・失効のいずれか）。');
    this.name = 'SyncAuthError';
  }
}

/** 404。所有者スコープ外は存在秘匿で 404 になる（他者の資料と「無い」を区別しない）。 */
export class SyncNotFoundError extends Error {
  constructor(readonly path: string) {
    super(`資料が見つかりません: ${path}`);
    this.name = 'SyncNotFoundError';
  }
}

/** 想定外の状態コード・読めない本文。 */
export class SyncProtocolError extends Error {
  constructor(
    readonly status: number,
    readonly path: string,
    detail: string,
  ) {
    super(`同期プロトコルの応答が不正です（HTTP ${status} ${path}）: ${detail}`);
    this.name = 'SyncProtocolError';
  }
}
