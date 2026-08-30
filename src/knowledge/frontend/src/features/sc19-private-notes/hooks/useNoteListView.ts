import { useMemo, useState } from 'react';
import { useNavigate, useSearch } from '@tanstack/react-router';
import type { PrivateNoteDto } from '@foundation/api/generated/bff.schemas';
import type { PrivateNotesSearch, TabOption } from '../routes/sc19PrivateNotesRoute';

// SC-19, UC-11, FR-19/FR-21: 一覧の見え方のクライアント状態
// （計画 13_frontend-stack §ディレクトリ構成 の `hooks/`）。
//
// **サーバー状態をここへ持ち込まない** —— 一覧の取得と更新は `api/usePrivateNotes.ts` の
// TanStack Query が持つ（ADR-0031）。ここに在るのは「取得済みの一覧のどこを見ているか」だけである。
//
// 🔴 **タブと絞り込み語の単一情報源は URL である**（`?tab=trash` / `?q=`。IADR-0124 決定 3）。
// 同じ状態を `stores/` のクライアントストアへ二重に持たない —— 計画が `?tab=trash` を
// 明示している以上、共有・再読込・戻るで同じ一覧になる性質を壊せない。
//
// ■ 🔴 **削除済みの件数バッジと「うち削除済み」は同じ応答から数える**（契約が「数え方を 2 つに
//   しない」と決めている）。だから `live` / `trashed` は 1 本の応答を分けるだけであり、
//   タブごとに問い合わせを分けない。

export interface NoteListView {
  search: PrivateNotesSearch;
  /** URL の検索パラメータを部分更新する。 */
  setParams: (patch: Partial<PrivateNotesSearch>) => void;
  /** タブを切り替える。**選択は持ち越さない**（別のタブの行を選んだままにしない）。 */
  switchTab: (tab: TabOption) => void;
  /** 削除されていない資料（件数バッジの母数）。 */
  live: PrivateNoteDto[];
  /** 削除済みの資料（件数バッジの母数）。 */
  trashed: PrivateNoteDto[];
  /** いま表示するタブに、タイトルの部分一致を掛けた行。 */
  rows: PrivateNoteDto[];
  /**
   * 「いま」。**描画のたびに読み直さない** —— 残り日数が描画のたびに揺れると、
   * 検査でも実運用でも同じ行が違う値を出しうる。
   */
  now: Date;
  /** 削除済みタブの一括操作で選んでいる ID。 */
  selected: string[];
  setSelected: (update: (prev: string[]) => string[]) => void;
  clearSelection: () => void;
}

/**
 * 個人資料の一覧を、タブ・絞り込み語・選択状態の側から見た形に整える。
 *
 * @param all 取得済みの全件（削除済みを含む 1 本の応答）
 */
export function useNoteListView(all: PrivateNoteDto[]): NoteListView {
  const search: PrivateNotesSearch = useSearch({ from: '/_shell/my/notes' });
  const navigate = useNavigate({ from: '/my/notes' });

  const [selected, setSelected] = useState<string[]>([]);

  const setParams = (patch: Partial<PrivateNotesSearch>) =>
    void navigate({ search: (prev: PrivateNotesSearch) => ({ ...prev, ...patch }) });

  const switchTab = (tab: TabOption) => {
    setSelected([]);
    setParams({ tab });
  };

  const live = useMemo(() => all.filter((n) => !n.deleted), [all]);
  const trashed = useMemo(() => all.filter((n) => n.deleted), [all]);

  // 絞り込み（05_screens §SC-19 主要素 6）。**タイトルの部分一致だけ**を実装している ——
  // タグ・公開範囲・同期状態は台帳（契約）に項目が無い（作業仕様書 §計画との差異）。
  const query = search.q.trim().toLowerCase();
  const rows = useMemo(() => {
    const source = search.tab === 'trash' ? trashed : live;
    if (query === '') return source;
    return source.filter((n) => n.title.toLowerCase().includes(query));
  }, [search.tab, live, trashed, query]);

  const now = useMemo(() => new Date(), []);

  return {
    search,
    setParams,
    switchTab,
    live,
    trashed,
    rows,
    now,
    selected,
    setSelected,
    clearSelection: () => setSelected([]),
  };
}
