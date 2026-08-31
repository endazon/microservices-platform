import { useState } from 'react';
import type { SampleFilter } from '../types';

// テンプレート: feature 固有のクライアント状態。計画 13_frontend-stack §ディレクトリ構成 の `hooks/`。
//
// **サーバー状態をここへ持ち込まない**（取得・キャッシュは `api/` の TanStack Query が持つ。ADR-0031）。
// 画面をまたいで共有するクライアント状態が要る場合は、計画が採用した **Zustand** のストアを
// feature の `stores/` へ置く（Zustand は #788 で導入済みである）。**本雛形に `stores/` の枠は無い**
// （#1100 / IADR-0321 で撤去した） —— **URL を単一情報源にする画面はストアを持たないのが既定**であり
// （IADR-0124 決定 3）、実在する feature で `stores/` に実体を持つものは 1 つも無い。
// **要ると分かった時点で `stores/` を作り、理由を PR 本文へ書く。**
// 空枠を先に置かない（計画 ADR-0065 決定 4 が `.gitkeep` の枠置き規範を撤回した。枠だけの状態は
// 「区分が揃っている」という**適合の見え方**を作る）。
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
