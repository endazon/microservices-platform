import { Trans, useLingui } from '@lingui/react/macro';
import { Link } from '@tanstack/react-router';
import { Button, Panel, Tag } from '@platform/ui';
import {
  getBffDocumentDetailQueryKey,
  useBffDocumentDetail,
} from '@foundation/api/generated/documents/documents';
import { okData } from '@foundation/api/orvalSelect';
import { formatDateTime } from '@foundation/utils/formatDateTime';
import type {
  DocumentDto,
  EdgeTypeCatalogItem,
  GraphEdgeItem,
  GraphNodeItem,
} from '@foundation/api/generated/bff.schemas';

// SC-18 主要素 5 (#917): ノード選択時のサイドパネル。
// タイトル / 種別 / 更新日 / タグ / 「文書を開く」導線 / 接続している辺の一覧。
//
// ■ 更新日とタグはグラフ応答に載っていない（200 ノード分を常に運ぶ理由が無い）。
//   選択された 1 件だけ既存の /bff/documents/{id} から引く。404（不在・権限による秘匿は
//   区別されない。IADR-0009）はパネルを壊さず、グラフ応答が持つ情報だけで表示する。
// ■ 「文書を開く」は SC-03（/docs/:id）へ遷移する（05_screens §SC-18 アクション）。
// ■ 器はモックの `.panel`（`Panel`）である —— 右側の側パネルは一覧に並ぶ独立した単位（`Card`）ではなく、
//   本文の**区画**である（Panel.tsx 冒頭の描き分け）。

export interface NodeSidePanelProps {
  node: GraphNodeItem;
  /** 表示中のグラフの辺（このノードに接続するものを集計して出す）。 */
  edges: readonly GraphEdgeItem[];
  edgeTypes: readonly EdgeTypeCatalogItem[];
  onClose: () => void;
}

export function NodeSidePanel({ node, edges, edgeTypes, onClose }: NodeSidePanelProps) {
  const { t } = useLingui();
  const detail = useBffDocumentDetail<DocumentDto, unknown>(node.documentId, {
    query: {
      queryKey: getBffDocumentDetailQueryKey(node.documentId),
      select: okData,
    },
  });

  const typeName = new Map(edgeTypes.map((tp) => [tp.id, tp.name]));
  const connected = edges.filter(
    (e) => e.sourceDocumentId === node.documentId || e.targetDocumentId === node.documentId,
  );
  // 型ごとの本数（例: cites 2・related 3）。順序を安定させる（表示が実行ごとに揺れない）。
  const byType = [
    ...connected.reduce((acc, e) => {
      const name = typeName.get(e.edgeTypeId) ?? e.edgeTypeId;
      return acc.set(name, (acc.get(name) ?? 0) + 1);
    }, new Map<string, number>()),
  ].sort(([a], [b]) => a.localeCompare(b));

  return (
    <Panel
      data-testid="node-side-panel"
      aria-label={t`選択中の文書`}
      className="mb-0"
      heading={
        <span className="flex items-start justify-between gap-2">
          <span className="text-sm font-medium text-fg">
            <span aria-hidden="true" className="mr-1">
              {node.isPrivateNote ? '👤' : '📄'}
            </span>
            {node.title}
          </span>
          <Button variant="ghost" size="sm" onClick={onClose} aria-label={t`閉じる`}>
            ✕
          </Button>
        </span>
      }
      headingAs="h2"
    >
      <div className="space-y-2 text-sm">
        <p>
          <span className="text-fg-muted">
            <Trans>種別:</Trans>
          </span>{' '}
          {/* FR-19, SC-18, 計画 ADR-0102 決定 5 (#1456): 🔴 **括弧書き「（自分のみ）」は付けない。**
              本画面は他の利用者が所有する個人資料も描き得る（所有者がグラフ表示を ON にし、閲覧者が読める場合）が、
              ノードの契約（`GraphNodeItem`）は所有者を運ばないため所有者を判別できない。決定 5 は「所有者以外に
              付さない」ことを求めており所有者へ付けることは義務づけていないので、**一律で外す**のが適合である。 */}
          {node.isPrivateNote ? <Trans>個人資料</Trans> : <Trans>組織文書</Trans>}
        </p>
        {detail.data && (
          <>
            <p>
              <span className="text-fg-muted">
                <Trans>更新日:</Trans>
              </span>{' '}
              {formatDateTime(detail.data.updatedAt)}
            </p>
            {detail.data.tags.length > 0 && (
              <p className="flex flex-wrap items-center gap-1">
                <span className="text-fg-muted">
                  <Trans>タグ:</Trans>
                </span>
                {detail.data.tags.map((tag) => (
                  <Tag key={tag}>{tag}</Tag>
                ))}
              </p>
            )}
          </>
        )}
        {detail.isError && (
          <p className="text-fg-muted">
            <Trans>文書の詳細は表示できません。</Trans>
          </p>
        )}
        <div>
          <p className="text-fg-muted">
            <Trans>接続している辺:</Trans>
          </p>
          {byType.length === 0 ? (
            <p>
              <Trans>表示中の辺はありません（孤立文書）。</Trans>
            </p>
          ) : (
            <ul className="list-inside list-disc">
              {byType.map(([name, count]) => (
                <li key={name}>
                  {name}: {count}
                </li>
              ))}
            </ul>
          )}
        </div>
        <p>
          <Link to="/docs/$id" params={{ id: node.documentId }} className="text-accent underline">
            <Trans>文書を開く</Trans>
          </Link>
        </p>
      </div>
    </Panel>
  );
}
