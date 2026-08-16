import { useState } from 'react';
import type { SampleFilter } from '../types';

// テンプレート: feature 固有のクライアント状態。計画 13_frontend-stack §ディレクトリ構成 の `hooks/`。
//
// **サーバー状態をここへ持ち込まない**（取得・キャッシュは `api/` の TanStack Query が持つ。ADR-0031）。
// 画面をまたいで共有するクライアント状態が要る場合は、計画が採用した **Zustand** のストアを
// feature の `stores/` へ置く（本雛形は Zustand が未導入のため `stores/` を持たない。
// 空フォルダを置かない規約 = src/README.md「存在しない区分のフォルダは作らない」に従う。
// 導入は SPA 移行第 4 段 / #493）。
//
// **絞り込み条件は URL を単一情報源にするのが望ましい**（TanStack Router の `validateSearch` ＋
// `useSearch({ from })`。IADR-0124 決定 3。共有・再読込・戻る操作で状態が失われない）。
// 本雛形は最小構成として useState で示す。

const EMPTY_FILTER: SampleFilter = { keyword: '' };

export const useSampleFilter = () => {
  const [filter, setFilter] = useState<SampleFilter>(EMPTY_FILTER);
  return {
    filter,
    setKeyword: (keyword: string) => setFilter({ keyword }),
    reset: () => setFilter(EMPTY_FILTER),
  };
};
