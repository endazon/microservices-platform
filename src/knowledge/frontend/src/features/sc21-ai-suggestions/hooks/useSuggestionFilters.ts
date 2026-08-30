import { useNavigate, useSearch } from '@tanstack/react-router';
import type { AiSuggestionSearch } from '../types/suggestionVocabulary';

// SC-21, UC-10, FR-18: 絞り込み条件のクライアント状態
// （計画 13_frontend-stack §ディレクトリ構成 の `hooks/`）。
//
// **サーバー状態をここへ持ち込まない** —— 提案の取得は `api/useAiSuggestions.ts` の
// TanStack Query が持ち、**絞りはサーバ側で適用される**（取得後にクライアントで間引かない）。
//
// 🔴 **URL が絞り込みの単一情報源である**（state / kind。IADR-0124 決定 3）。
// **クライアント状態ストアを持ち込まない** —— 共有・再読込・戻るのいずれでも同じ一覧になる。
// 本 feature に `stores/` が無いのはこの理由による（枠だけを置くこともしない）。

export interface SuggestionFilters {
  search: AiSuggestionSearch;
  /** URL の検索パラメータを部分更新する（絞り込みの書き込み口はここだけ）。 */
  setParams: (patch: Partial<AiSuggestionSearch>) => void;
}

export function useSuggestionFilters(): SuggestionFilters {
  const search: AiSuggestionSearch = useSearch({ from: '/_shell/ai-suggestions' });
  const navigate = useNavigate({ from: '/ai-suggestions' });

  return {
    search,
    setParams: (patch: Partial<AiSuggestionSearch>) =>
      void navigate({ search: (prev: AiSuggestionSearch) => ({ ...prev, ...patch }) }),
  };
}
