import { useMemo, useState } from 'react';
import type { GraphNodeItem } from '@foundation/api/generated/bff.schemas';

// SC-18, UC-10, FR-17: グラフ内検索と選択ノード（計画 13_frontend-stack §ディレクトリ構成 の `hooks/`）。
//
// **画面を閉じたら消えてよいローカル状態だけを持つ。** 探索条件（URL）とは別物である ——
// グラフ内検索は**新たな探索を起こさない**。対象は「権限内で既に表示されているノード」に限る
// （05_screens §SC-18 主要素 7）。この一点があるため、検索語を URL へ載せていない
// （載せると「共有された URL が別の探索を起こす」と読めてしまう）。

/** グラフ内検索で提示する上限（先頭一致へフォーカスするための補助であり、一覧ではない）。 */
const NODE_MATCH_LIMIT = 8;

export interface GraphNodeSearch {
  nodeQuery: string;
  setNodeQuery: (value: string) => void;
  /** タイトル部分一致で絞った取得済みノード（最大 8 件）。 */
  matches: GraphNodeItem[];
  /** 一致の先頭。キャンバスのフォーカス指定に使う（一致なしなら `undefined`）。 */
  focusedId: string | undefined;
  selectedId: string | null;
  setSelectedId: (id: string | null) => void;
}

/**
 * グラフ内検索（タイトル部分一致）と、サイドパネルに出す選択ノードを保持する。
 *
 * @param nodes 取得済みのノード（絞り込みの母集合）
 */
export function useGraphNodeSearch(nodes: GraphNodeItem[]): GraphNodeSearch {
  const [nodeQuery, setNodeQuery] = useState('');
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const matches = useMemo(() => {
    const q = nodeQuery.trim().toLowerCase();
    if (q === '') return [];
    return nodes.filter((n) => n.title.toLowerCase().includes(q)).slice(0, NODE_MATCH_LIMIT);
  }, [nodes, nodeQuery]);

  return {
    nodeQuery,
    setNodeQuery,
    matches,
    focusedId: matches[0]?.documentId,
    selectedId,
    setSelectedId,
  };
}
