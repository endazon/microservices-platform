// FR-20, UC-11, ADR-0037 決定 2・7・14, [[IADR-0270]] 決定 3・7, [[IADR-0338]] 決定 4, [[IADR-0352]]:
// DocumentService の同期プロトコル（/private-notes/sync/*）の契約の写し。
//
// 🔴 契約の正はサーバ（`Features/ObsidianSync/{Manifest,Pull,Push,Delete}`）と
// `docs/api/FR-20_obsidian-sync.md` であり、**ここを変えてサーバを動かさない**（#1098 / #1153 補足・制約）。
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

/** push の `edits[]` の 1 要素。**1 要素 = 1 版**（ADR-0037 決定 8）。 */
export interface SyncEdit {
  content: string;
  editedAt?: string;
  changeNote?: string;
}

/** push の要求。`noteId` 無し = 新規、有り = 更新（更新は `baseVersion` 必須＝楽観ロック）。 */
export interface PushNoteRequest {
  noteId: string | null;
  vaultPath: string;
  title: string;
  baseVersion: number | null;
  edits: SyncEdit[];
}

export interface PushNoteResponse {
  noteId: string;
  version: number;
  contentHash: string;
  bytes: number;
}

export interface DeleteNoteResponse {
  deletedAt: string;
  purgeAt: string;
}

/**
 * リネーム（`vaultPath` の更新）の要求。**本文は運ばない**（中身は push が送る）。
 * `version` は最後に見た版で、楽観ロックのためだけに使う —— サーバはリネームで版を進めない。
 */
export interface MoveNoteRequest {
  vaultPath: string;
  version: number;
}

export interface MoveNoteResponse {
  noteId: string;
  vaultPath: string;
  version: number;
  updatedAt: string;
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

/**
 * 409。サーバは 3 つの形で返す（`Push/Endpoint.cs` / `Move/Endpoint.cs` /
 * `PrivateNoteEndpoints.PathConflictProblem`）:
 * - `version_conflict`（`baseVersion`／`version` と現在版の不一致。**自動解決しない**。ADR-0037 決定 7）
 * - `deleted`（対象がサーバ側で論理削除済み）
 * - `vault_path_conflict`（新規作成・リネームのパスが既存の有効な資料と重なる）
 *
 * **push と move は同じ 3 形を返す**（[[IADR-0353]] 決定 2・3）ので、解析は 1 本で足りる。
 */
export type SyncConflict =
  | { error: 'version_conflict'; serverVersion: number; serverUpdatedAt: string }
  | { error: 'deleted'; purgeAt: string | null }
  | { error: 'vault_path_conflict'; vaultPath: string };

export class SyncConflictError extends Error {
  constructor(
    readonly path: string,
    readonly conflict: SyncConflict,
  ) {
    super(`競合（409 ${path}）: ${conflict.error}`);
    this.name = 'SyncConflictError';
  }
}

/** 413。本文が 1 MB を超える（切り詰めずに拒否される。[[IADR-0270]] 決定 7）。 */
export class SyncTooLargeError extends Error {
  constructor(readonly path: string) {
    super('本文が上限（1 MB）を超えているため送れません。');
    this.name = 'SyncTooLargeError';
  }
}

/** 507。容量 100% での新規作成（ADR-0037 決定 17。更新は通る）。 */
export class SyncQuotaError extends Error {
  constructor(readonly path: string) {
    super('保存容量の上限に達しているため新規作成できません（既存資料の更新はできます）。');
    this.name = 'SyncQuotaError';
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
