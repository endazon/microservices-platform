import { useNavigate, useSearch } from '@tanstack/react-router';
import type { EdgeTypeCatalogItem } from '@foundation/api/generated/bff.schemas';
import type { GraphSearch } from '../routes/sc18GraphRoute';

// SC-18, UC-10, FR-17: 探索条件のクライアント状態（計画 13_frontend-stack §ディレクトリ構成 の `hooks/`）。
//
// **サーバー状態をここへ持ち込まない** —— 近傍の取得と辺の型辞書は `api/useGraphView.ts` の
// TanStack Query が持つ（ADR-0031）。ここに在るのは「何を問い合わせるか」だけである。
//
// 🔴 **探索条件の単一情報源は URL である**（root / hops / by / types。IADR-0124 決定 3）。
// 同じ条件を `stores/` のクライアントストアへ二重に持たない —— 共有・再読込・戻るの
// いずれでも同じグラフになる、という性質が失われる。

export interface GraphFilters {
  search: GraphSearch;
  /** URL の検索パラメータを部分更新する（探索条件の書き込み口はここだけ）。 */
  setParams: (patch: Partial<GraphSearch>) => void;
  /** 現在有効な辺の型 ID。URL に `types` が無い＝全型 ON（サーバの既定と一致）。 */
  activeTypes: string[];
  /** 🔴 最後の 1 つは外せない（全 OFF は「何も描かない」であり、探索として意味を持たない）。 */
  lastActive: boolean;
  toggleType: (typeId: string) => void;
}

/**
 * 探索条件（起点・深さ・間引き・辺の型フィルタ）を URL の上で読み書きする。
 *
 * @param catalog 辺の型辞書（`api/useGraphView.ts` の取得結果。未取得のあいだは空配列）
 */
export function useGraphFilters(catalog: EdgeTypeCatalogItem[]): GraphFilters {
  const search: GraphSearch = useSearch({ from: '/_shell/graph' });
  const navigate = useNavigate({ from: '/graph' });

  const setParams = (patch: Partial<GraphSearch>) =>
    void navigate({ search: (prev: GraphSearch) => ({ ...prev, ...patch }) });

  // 辺の型フィルタ（SC-18 主要素 4）: URL の types が単一情報源。省略＝全型 ON。
  const activeTypes = search.types ?? catalog.map((type) => type.id);

  const toggleType = (typeId: string) => {
    const next = activeTypes.includes(typeId)
      ? activeTypes.filter((id) => id !== typeId)
      : [...activeTypes, typeId];
    // 全 ON は「絞りなし」として types を URL から外す（サーバの既定と一致させる）。
    setParams({ types: next.length === catalog.length ? undefined : next });
  };

  return { search, setParams, activeTypes, lastActive: activeTypes.length === 1, toggleType };
}
