// FR-20, ADR-0037 決定 4, [[IADR-0338]] 決定 1・9, [[IADR-0352]]: サーバの `vaultPath` とローカル
// （Vault 内）のパスを相互に落とす規則。
//
// サーバは `vaultPath` を文字列として受けるだけで形を検証しない（push 側の契約）。Vault へ書く側で
// **Vault の外へ出るパス**（絶対パス・`..`）と Obsidian が扱えない文字を弾き、拡張子が無ければ `.md` を
// 補う。同期フォルダ（決定 4: 対象は特定フォルダのみ）の配下にそろえる。

export type ResolvedLocalPath =
  | { ok: true; path: string }
  | { ok: false; reason: 'empty' | 'absolute' | 'traversal' | 'control-char' };

// 制御文字（U+0000〜U+001F・U+007F）。ソースに生のバイトを置かないため文字コードから組む。
const CONTROL_CHARS = new RegExp(
  `[${String.fromCharCode(0)}-${String.fromCharCode(31)}${String.fromCharCode(127)}]`,
);
const DRIVE_LETTER = /^[A-Za-z]:/;

/** Vault 内の相対パスへ正規化する（区切りは `/`）。空文字は「Vault 直下」を意味する。 */
export function normalizeFolder(folder: string): string {
  return folder
    .trim()
    .replace(/\\/g, '/')
    .split('/')
    .filter((s) => s !== '' && s !== '.')
    .join('/');
}

export function resolveLocalPath(syncFolder: string, vaultPath: string): ResolvedLocalPath {
  const raw = vaultPath.trim().replace(/\\/g, '/');
  if (raw === '') return { ok: false, reason: 'empty' };
  if (CONTROL_CHARS.test(raw)) return { ok: false, reason: 'control-char' };
  if (raw.startsWith('/') || DRIVE_LETTER.test(raw)) return { ok: false, reason: 'absolute' };

  const segments = raw.split('/').filter((s) => s !== '' && s !== '.');
  if (segments.length === 0) return { ok: false, reason: 'empty' };
  if (segments.some((s) => s === '..')) return { ok: false, reason: 'traversal' };

  const last = segments[segments.length - 1]!;
  if (!/\.[^.]+$/.test(last)) segments[segments.length - 1] = `${last}.md`;

  const folder = normalizeFolder(syncFolder);
  const relative = segments.join('/');
  return { ok: true, path: folder === '' ? relative : `${folder}/${relative}` };
}

/** `a/b/c.md` → `['a', 'a/b']`。書き込み前に親フォルダを作るため。 */
export function parentFolders(path: string): string[] {
  const parts = path.split('/');
  const out: string[] = [];
  for (let i = 1; i < parts.length; i += 1) out.push(parts.slice(0, i).join('/'));
  return out;
}

/** ローカルパスが同期フォルダの配下か（フォルダが空なら Vault 全体）。 */
export function isInFolder(syncFolder: string, localPath: string): boolean {
  const folder = normalizeFolder(syncFolder);
  return folder === '' ? true : localPath.startsWith(`${folder}/`);
}

/** ローカルパス → サーバへ送る `vaultPath`（同期フォルダを剥がした相対パス）。配下でなければ null。 */
export function toVaultPath(syncFolder: string, localPath: string): string | null {
  const folder = normalizeFolder(syncFolder);
  if (folder === '') return localPath;
  return localPath.startsWith(`${folder}/`) ? localPath.slice(folder.length + 1) : null;
}

/** `a/b/メモ.md` → `メモ`。新規 push の title（サーバは title 必須）。 */
export function titleOf(localPath: string): string {
  const base = localPath.split('/').pop() ?? localPath;
  const title = base.replace(/\.[^.]+$/, '');
  return title === '' ? base : title;
}

/** 「両方残す」の複製先。`a/b.md` + `20260903-0912` → `a/b (ローカル 20260903-0912).md`。 */
export function localCopyPath(localPath: string, stamp: string): string {
  const slash = localPath.lastIndexOf('/');
  const dir = slash >= 0 ? localPath.slice(0, slash + 1) : '';
  const base = slash >= 0 ? localPath.slice(slash + 1) : localPath;
  const dot = base.lastIndexOf('.');
  const stem = dot > 0 ? base.slice(0, dot) : base;
  const ext = dot > 0 ? base.slice(dot) : '';
  return `${dir}${stem} (ローカル ${stamp})${ext}`;
}

/** `Date` → `YYYYMMDD-HHmm`（ローカル時刻）。 */
export function stampOf(date: Date): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}${p(date.getMonth() + 1)}${p(date.getDate())}-${p(date.getHours())}${p(date.getMinutes())}`;
}
