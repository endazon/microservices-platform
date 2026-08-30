import { useMemo } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Alert, Button, Input, Label, Select } from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';
import type { GraphNodeItem } from '@foundation/api/generated/bff.schemas';
import { useEdgeTypeCatalog, useGraphNeighbors } from '../api/useGraphView';
import { useGraphFilters } from '../hooks/useGraphFilters';
import { useGraphNodeSearch } from '../hooks/useGraphNodeSearch';
import { buildGraphOption } from '../types/graphOption';
import { HOPS_OPTIONS, THINNING_OPTIONS } from '../routes/sc18GraphRoute';
import type { GraphSearch, ThinningOption } from '../routes/sc18GraphRoute';
import { GraphCanvas } from './GraphCanvas';
import { GraphLegend } from './GraphLegend';
import { NodeSidePanel } from './NodeSidePanel';

// SC-18, UC-10, FR-17/FR-05: ナレッジグラフビュー（05_screens: ルート /graph）。**読み取り専用** ——
// グラフ上での辺の追加・削除は行わない（辺の作成は SC-03 / SC-19 の導線が担う）。
//
// ■ URL（root / hops / by / types）が探索条件の単一情報源である（IADR-0126 決定 3 と同じ作法）。
// ■ 🔴 辺の型フィルタは**サーバ側で適用**される（planning#446）。ここでは URL → クエリの写像だけを行う。
// ■ 空状態は 2 種を描き分ける（SC-18 主要素 8）:
//     404（不在・権限は区別されない） → 「権限のある文書がありません」
//     200 で辺 0 本                   → 「関係する文書がありません」
//   さらに root 未指定は「起点の指定を促す案内」（探索をまだ始めていない状態であり、上の 2 種とは別）。
// ■ ヘルプ固定文言（ADR-0034 決定 2 の受け入れ済み副作用）は**結果が 0 件でないときにも常に**出す
//   —— 表示された関係が全体の一部でしかない可能性を利用者へ常に伝える（05_screens §SC-18）。

export function GraphViewPage() {
  const { t } = useLingui();

  const edgeTypes = useEdgeTypeCatalog();
  // 辺の型フィルタの選択肢は辞書が持つ（ADR-0033 決定 9。改名に追随させる）。
  const catalog = edgeTypes.data ?? [];

  // 探索条件（URL が単一情報源）と、グラフ内検索・選択ノード（ローカル状態）は hooks/ が持つ。
  const { search, setParams, activeTypes, lastActive, toggleType } = useGraphFilters(catalog);
  const neighbors = useGraphNeighbors(search);

  const view = neighbors.data;
  const nodes = useMemo(() => view?.nodes ?? [], [view]);
  const edges = useMemo(() => view?.edges ?? [], [view]);

  const { nodeQuery, setNodeQuery, matches, focusedId, selectedId, setSelectedId } =
    useGraphNodeSearch(nodes);

  // ［2026-08-30 / #1078］翻訳文へ差し込む値は**単純な変数**として渡す。
  // `{nodes.length}` のようなプロパティ参照を `<Trans>` へ直接書くと、lingui が
  // プレースホルダ名を機械的に付け直すため（`{0}` 等）、**カタログの msgid が
  // 実装の書き方に依存して揺れる**（`lingui/no-expression-in-message`）。
  const shownCount = nodes.length;
  const totalCount = view?.totalNodes ?? 0;
  const edgeCount = edges.length;

  // 権限外・不在は同じ 404 で秘匿される（ADR-0034 決定 2）。どちらかは区別できない。
  const deniedOrMissing =
    neighbors.error instanceof ApiError && neighbors.error.kind === 'notFound';
  const hasGraph = !!view && nodes.length > 0 && edges.length > 0;
  const noRelations = !!view && edges.length === 0;

  const option = useMemo(
    () =>
      buildGraphOption({
        nodes,
        edges,
        edgeTypes: edgeTypes.data ?? [],
        originId: search.root || undefined,
        focusedId,
        labels: {
          organization: t`組織文書`,
          privateNote: t`個人資料（自分のみ）`,
          isolated: t`孤立文書（表示中の辺なし）`,
        },
      }),
    [nodes, edges, edgeTypes.data, search.root, focusedId, t],
  );

  const selectedNode: GraphNodeItem | undefined = nodes.find((n) => n.documentId === selectedId);

  return (
    <section className="space-y-3">
      <div>
        <h1 className="text-lg font-semibold text-[--color-fg]">
          <Trans>ナレッジグラフ</Trans>
        </h1>
        {/* ヘルプ固定文言: 0 件でないときにも常に出す（上の冒頭注記）。 */}
        <p className="text-xs text-[--color-fg-muted]" data-testid="graph-help">
          <Trans>
            関係が表示されない場合、関係が存在しないのか、閲覧権限がないのかは区別できません。
            これは、閲覧権限のない文書の存在を知られないようにするための仕様です。
            個人資料が表示されるのは、所有者が「ナレッジグラフに表示する」を ON
            にした資料のみです（既定 OFF）。AI 提案の辺は承認済みのみ表示されます。
          </Trans>
        </p>
      </div>

      <div className="flex flex-wrap items-end gap-4">
        <div>
          <Label htmlFor="graph-hops">
            <Trans>探索深さ（hops）</Trans>
          </Label>
          {/* 1 / 2 / 3 のみ（既定 2・上限 3）。丸めずエラーの防壁はサーバ（400）に在るが、
              UI は範囲外を作れない形にする（05_screens §SC-18 入力/バリデーション）。 */}
          <Select
            id="graph-hops"
            selectSize="sm"
            value={search.hops}
            onChange={(e) => setParams({ hops: Number(e.target.value) as GraphSearch['hops'] })}
          >
            {HOPS_OPTIONS.map((h) => (
              <option key={h} value={h}>
                {h === 2 ? t`${h}（既定）` : h === 3 ? t`${h}（上限）` : String(h)}
              </option>
            ))}
          </Select>
        </div>
        <div>
          <Label htmlFor="graph-by">
            <Trans>間引きの基準</Trans>
          </Label>
          <Select
            id="graph-by"
            selectSize="sm"
            value={search.by}
            onChange={(e) => setParams({ by: e.target.value as ThinningOption })}
          >
            {THINNING_OPTIONS.map((b) => (
              <option key={b} value={b}>
                {b === 'distance'
                  ? t`起点からの距離が近い順`
                  : b === 'updated'
                    ? t`更新日が新しい順`
                    : t`次数が大きい順`}
              </option>
            ))}
          </Select>
        </div>
        <div className="min-w-48">
          <Label htmlFor="graph-node-search">
            <Trans>グラフ内検索</Trans>
          </Label>
          <Input
            id="graph-node-search"
            inputSize="sm"
            value={nodeQuery}
            placeholder={t`表示中のノードをタイトルで絞り込む`}
            onChange={(e) => setNodeQuery(e.target.value)}
          />
        </div>
      </div>

      {catalog.length > 0 && (
        <fieldset
          className="flex flex-wrap items-center gap-3 text-sm"
          data-testid="edge-type-filter"
        >
          <legend className="float-left mr-2 text-xs text-[--color-fg-muted]">
            <Trans>辺の型:</Trans>
          </legend>
          {catalog.map((tp) => {
            const active = activeTypes.includes(tp.id);
            return (
              <label key={tp.id} className="inline-flex items-center gap-1">
                <input
                  type="checkbox"
                  checked={active}
                  disabled={active && lastActive}
                  onChange={() => toggleType(tp.id)}
                />
                {tp.name}
              </label>
            );
          })}
        </fieldset>
      )}

      {nodeQuery.trim() !== '' && (
        <div className="text-sm" data-testid="node-search-results">
          {matches.length === 0 ? (
            <p role="status">
              <Trans>該当するノードがありません。</Trans>
            </p>
          ) : (
            <ul className="flex flex-wrap gap-2">
              {matches.map((n) => (
                <li key={n.documentId}>
                  <Button variant="secondary" size="sm" onClick={() => setSelectedId(n.documentId)}>
                    {n.title}
                  </Button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {/* 表示上限と間引きの表示（SC-18 主要素 6 / ADR-0049）。
          「もっと読み込む」は置かない —— フィルタを絞ることを促す（無制限展開は許さない）。 */}
      {view?.truncated && (
        <Alert tone="warning" label={t`表示上限`} data-testid="truncation-banner">
          {view.totalIsLowerBound ? (
            <Trans>
              上位 {shownCount} 件を表示（全 {totalCount} 件以上）。総数の探索も上限に達した
              ため、「更新日が新しい順」「次数が大きい順」は厳密な上位 {shownCount}{' '}
              件ではありません。 探索深さを浅くするか、辺の型を絞ってください。
            </Trans>
          ) : (
            <Trans>
              上位 {shownCount} 件を表示（全 {totalCount} 件）。すべては表示していません。
              探索深さを浅くするか、辺の型を絞ってください。
            </Trans>
          )}
        </Alert>
      )}

      {search.root === '' && (
        <Alert tone="info" label={t`起点が未指定です`}>
          <Trans>
            ナレッジグラフは起点となる文書から関係をたどります。検索結果や文書詳細から
            「ナレッジグラフで表示」を選ぶか、URL の root パラメータで起点を指定してください。
          </Trans>
        </Alert>
      )}

      {deniedOrMissing && (
        <Alert tone="info" label={t`権限のある文書がありません`} data-testid="empty-denied">
          <Trans>
            指定された起点の文書は表示できません。文書が存在しないのか、閲覧権限がないのかは
            区別できません。
          </Trans>
        </Alert>
      )}

      {noRelations && !deniedOrMissing && (
        <Alert tone="info" label={t`関係する文書がありません`} data-testid="empty-no-relations">
          <Trans>この文書には表示できる関係がありません。</Trans>
        </Alert>
      )}

      {neighbors.isError && !deniedOrMissing && (
        <Alert tone="danger" label={t`読み込みエラー`}>
          <Trans>グラフを読み込めませんでした。時間をおいて再試行してください。</Trans>
        </Alert>
      )}

      {neighbors.isPending && search.root !== '' && (
        <p role="status" className="text-sm text-[--color-fg-muted]">
          <Trans>読み込み中…</Trans>
        </p>
      )}

      {hasGraph && (
        <div className="flex flex-col gap-3 lg:flex-row">
          {/* グラフ描画領域が主役（画面の 7 割以上。SC-18 主要素 1）。 */}
          <div className="min-w-0 flex-[3] space-y-2">
            <GraphCanvas
              option={option}
              ariaLabel={t`ナレッジグラフ（ノード ${shownCount} 件・辺 ${edgeCount} 本）。詳細は凡例と選択パネルを参照`}
              onNodeClick={setSelectedId}
            />
            <GraphLegend />
          </div>
          {selectedNode && (
            <div className="flex-1">
              <NodeSidePanel
                node={selectedNode}
                edges={edges}
                edgeTypes={catalog}
                onClose={() => setSelectedId(null)}
              />
            </div>
          )}
        </div>
      )}
    </section>
  );
}
