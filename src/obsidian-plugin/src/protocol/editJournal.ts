// FR-20, ADR-0037 決定 4・5・8（フォローアップ 5）, [[IADR-0352]] 決定 1・2:
// 「1 編集」の刻みと、Obsidian 側の削除・リネーム・フォルダ外への移動の記録（journal）。
//
// **1 編集 = 1 版**（決定 8）。Obsidian は入力が止まるたびに保存する（`vault.on('modify')` が保存ごとに
// 発火する）ので、保存イベントをそのまま版にすると 1 段落を書く間に何十版も刻まれる。
// 静穏窓（`EDIT_QUIET_MS`）の間に続いた保存は**同じ編集**として畳み込み（内容は最後の保存のもの）、
// 窓を超えて空いたら**次の編集**にする。オフラインで 10 回（各 30 秒以上空けて）保存すれば 10 編集
// → push の `edits[]` が 10 要素 → サーバに 10 版。
//
// journal は `data.json` に本文つきで積む（トークンは持たない）。1 ファイル `MAX_PENDING_EDITS` 件を超えたら
// 古いものから落とす（サーバも直近 50 版しか保持しない。決定 16）。
//
// 純粋関数（引数の journal を書き換えて返す）。Obsidian のイベントは `main.ts` がここへ写す。

export const EDIT_QUIET_MS = 30_000;
export const MAX_PENDING_EDITS = 50;

export interface JournalEdit {
  /** ISO 8601。畳み込み後は最後の保存時刻。 */
  at: string;
  content: string;
}

export interface EditJournal {
  /** localPath → 未送信の編集列（古い順）。 */
  edits: Record<string, JournalEdit[]>;
  /** 追跡中のパス（state.localPath）が Obsidian 側で削除された。push で論理削除を送る。 */
  deleted: Record<string, true>;
  /** 追跡中のパスが同期フォルダの外へ移された。push で追跡を外すだけ（削除は送らない。決定 4）。 */
  movedOut: Record<string, true>;
  /** 新しいパス → 追跡中の元のパス（フォルダ内のリネーム。紐付けだけ更新する）。 */
  renamed: Record<string, string>;
}

export function emptyJournal(): EditJournal {
  return { edits: {}, deleted: {}, movedOut: {}, renamed: {} };
}

/** `data.json` から読んだ値を journal の形にそろえる（欠けや型違いは空にする）。 */
export function readJournal(raw: unknown): EditJournal {
  const record = typeof raw === 'object' && raw !== null ? (raw as Record<string, unknown>) : {};
  const journal = emptyJournal();
  const edits = isRecord(record.edits) ? record.edits : {};
  for (const [path, list] of Object.entries(edits)) {
    if (!Array.isArray(list)) continue;
    const valid = list.filter(
      (e): e is JournalEdit =>
        isRecord(e) && typeof e.at === 'string' && typeof e.content === 'string',
    );
    if (valid.length > 0) journal.edits[path] = valid;
  }
  for (const key of ['deleted', 'movedOut'] as const) {
    const flags = isRecord(record[key]) ? record[key] : {};
    for (const [path, v] of Object.entries(flags)) if (v === true) journal[key][path] = true;
  }
  const renamed = isRecord(record.renamed) ? record.renamed : {};
  for (const [to, from] of Object.entries(renamed)) {
    if (typeof from === 'string' && from !== '') journal.renamed[to] = from;
  }
  return journal;
}

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null;

/** 保存イベント。静穏窓の中なら直前の編集へ畳み込む。 */
export function recordSave(
  journal: EditJournal,
  path: string,
  content: string,
  at: Date,
  quietMs: number = EDIT_QUIET_MS,
): EditJournal {
  delete journal.deleted[path];
  const list = journal.edits[path] ?? [];
  const last = list[list.length - 1];
  if (last !== undefined && at.getTime() - Date.parse(last.at) < quietMs) {
    last.content = content;
    last.at = at.toISOString();
  } else {
    list.push({ at: at.toISOString(), content });
    while (list.length > MAX_PENDING_EDITS) list.shift();
  }
  journal.edits[path] = list;
  return journal;
}

/** 削除イベント。リネーム後に消された場合は**元の追跡パス**に印を付ける。 */
export function recordDelete(journal: EditJournal, path: string): EditJournal {
  delete journal.edits[path];
  const original = journal.renamed[path];
  if (original !== undefined) {
    delete journal.renamed[path];
    journal.deleted[original] = true;
  } else {
    journal.deleted[path] = true;
  }
  return journal;
}

/**
 * リネーム／移動イベント。
 * - フォルダ内 → フォルダ内: 紐付けの更新（元のパスを引き継ぐ。元へ戻したら記録を消す）
 * - フォルダ内 → フォルダ外: **同期停止**（削除ではない。決定 4）
 * - フォルダ外 → フォルダ内: 新規として次の push が拾う（記録は要らない）
 */
export function recordRename(
  journal: EditJournal,
  from: string,
  to: string,
  where: { fromInFolder: boolean; toInFolder: boolean },
): EditJournal {
  if (!where.fromInFolder) {
    if (where.toInFolder) delete journal.deleted[to];
    return journal;
  }
  const original = journal.renamed[from] ?? from;
  delete journal.renamed[from];
  const pending = journal.edits[from];
  delete journal.edits[from];

  if (!where.toInFolder) {
    journal.movedOut[original] = true;
    return journal;
  }
  if (original !== to) journal.renamed[to] = original;
  if (pending !== undefined && pending.length > 0) {
    journal.edits[to] = [...(journal.edits[to] ?? []), ...pending];
  }
  delete journal.deleted[to];
  return journal;
}

/** 送り終えた（または追跡を外した）パスの記録を消す。 */
export function clearPath(journal: EditJournal, path: string): EditJournal {
  delete journal.edits[path];
  delete journal.deleted[path];
  delete journal.movedOut[path];
  delete journal.renamed[path];
  for (const [to, from] of Object.entries(journal.renamed)) {
    if (from === path) delete journal.renamed[to];
  }
  return journal;
}

/** 元の追跡パス → 新しいパス（`renamed` の逆引き）。 */
export function localRenames(journal: EditJournal): Map<string, string> {
  const map = new Map<string, string>();
  for (const [to, from] of Object.entries(journal.renamed)) map.set(from, to);
  return map;
}
