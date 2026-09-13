import { useMemo } from 'react';
import type { ReactNode } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Button, EmptyState, Input, Label, Note, Select, Tag } from '@platform/ui';
import { QueryState } from '@foundation/ui/QueryState';
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
          // FR-19, SC-18, 計画 ADR-0102 決定 5 (#1456): 括弧書き「（自分のみ）」は付けない
          // （所有者を判別できない面であるため一律で外す。`NodeSidePanel` と同じ理由）。
          privateNote: t`個人資料`,
          isolated: t`孤立文書（表示中の辺なし）`,
        },
      }),
    [nodes, edges, edgeTypes.data, search.root, focusedId, t],
  );

  const selectedNode: GraphNodeItem | undefined = nodes.find((n) => n.documentId === selectedId);
  const originTitle = nodes.find((n) => n.documentId === search.root)?.title;

  /** グラフ本体（打ち切りの注記・描画領域・凡例・選択パネル）。 */
  function graphBody(): ReactNode {
    return (
      <div className="space-y-3">
        {/* 表示上限と間引きの表示（SC-18 主要素 6 / ADR-0049）。モックは `.note`（警告色）で出す。
            「もっと読み込む」は置かない —— フィルタを絞ることを促す（無制限展開は許さない）。 */}
        {view?.truncated && (
          <Note tone="warn" data-testid="truncation-banner">
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
          </Note>
        )}

        <div className="flex flex-col gap-3 lg:flex-row">
          {/*
            🔴 グラフ描画領域が主役である（画面の 7 割以上。SC-18 主要素 1）。
            **`Panel` で囲まない** —— 面と余白が入ると図が縮み、主役が脇へ退く。
            高さは `min-h` で床を作る（内容の少ない探索でも図の場所が痩せない）。
          */}
          <div className="flex min-h-[70vh] min-w-0 flex-[3] flex-col gap-2">
            <GraphCanvas
              className="min-h-[calc(70vh-9rem)] w-full flex-1 rounded-md border border-divider bg-bg"
              option={option}
              ariaLabel={t`ナレッジグラフ（ノード ${shownCount} 件・辺 ${edgeCount} 本）。詳細は凡例と選択パネルを参照`}
              onNodeClick={setSelectedId}
            />
            {/* 凡例は**常時**出す（折りたたまない。利用者裁定・質問票 第11回 Q3）。 */}
            <GraphLegend />
          </div>
          {selectedNode && (
            <div className="w-full lg:w-72 lg:shrink-0">
              <NodeSidePanel
                node={selectedNode}
                edges={edges}
                edgeTypes={catalog}
                onClose={() => setSelectedId(null)}
              />
            </div>
          )}
        </div>
      </div>
    );
  }

  /** 起点未指定 → 404（空） → 三部品（QueryState）の順に描き分ける。 */
  function renderResult(): ReactNode {
    if (search.root === '') {
      return (
        <EmptyState
          title={t`起点が未指定です`}
          description={t`検索結果や文書詳細から「ナレッジグラフで表示」を選ぶか、URL の root パラメータで起点を指定してください。`}
        />
      );
    }
    if (deniedOrMissing) {
      return (
        <div data-testid="empty-denied">
          <EmptyState
            title={t`権限のある文書がありません`}
            description={t`指定された起点の文書は表示できません。文書が存在しないのか、閲覧権限がないのかは区別できません。`}
          />
        </div>
      );
    }
    return (
      <QueryState
        query={neighbors}
        isEmpty={(data) => data.edges.length === 0}
        loadingLabel={t`グラフを読み込み中…`}
        errorTitle={t`グラフを読み込めませんでした。`}
        empty={
          <div data-testid="empty-no-relations">
            <EmptyState
              title={t`関係する文書がありません`}
              description={t`この文書には表示できる関係がありません。辺の型の絞り込みを緩めるか、探索深さを深くしてください。`}
            />
          </div>
        }
      >
        {() => graphBody()}
      </QueryState>
    );
  }

  return (
    <section className="space-y-3">
      <div className="flex flex-wrap items-center gap-n2">
        <h1 className="flex-1 text-[17px] font-medium text-fg">
          <Trans>ナレッジグラフ</Trans>
        </h1>
        {/* 起点の強調（モック上部の「起点: … ✕」）。解除すると探索前の案内へ戻る。 */}
        {search.root !== '' && (
          <>
            <Tag tone="accent">
              {originTitle === undefined ? t`起点: 指定あり` : t`起点: ${originTitle}`}
            </Tag>
            <Button variant="ghost" size="sm" onClick={() => setParams({ root: '' })}>
              <Trans>起点を解除する</Trans>
            </Button>
          </>
        )}
      </div>

      {/* ヘルプ固定文言: 0 件でないときにも常に出す（上の冒頭注記）。 */}
      <Note data-testid="graph-help">
        <Trans>
          関係が表示されない場合、関係が存在しないのか、閲覧権限がないのかは区別できません。
          これは、閲覧権限のない文書の存在を知られないようにするための仕様です。
          個人資料が表示されるのは、所有者が「ナレッジグラフに表示する」を ON
          にした資料のみです（既定 OFF）。AI 提案の辺は承認済みのみ表示されます。
        </Trans>
      </Note>

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
          <legend className="float-left mr-2 text-xs text-fg-muted">
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
            // 🔴 `role="status"` の直書きをやめ、空は空の部品で描く（失敗と同じ見た目にしない）。
            <EmptyState
              className="py-n3"
              title={t`該当するノードがありません。`}
              description={t`検索語を短くするか、辺の型の絞り込みを緩めてください。`}
            />
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

      {/*
        待ち・失敗・空・本体の描き分けは `QueryState` に一本化した（判定順は失敗 → 待ち → 空 → 本体）。
        ただし**この画面には QueryState の手前に 2 つの状態が在る**:
          1. 起点が未指定（探索をまだ始めていない。照会も送っていない ＝「待ち」ではない）
          2. 404（不在と権限は区別されない。ADR-0034 決定 2）——これは**失敗ではなく空**であり、
             再試行ボタンを出してはならない（押しても権限は生えない）。
        どちらも QueryState の内側では表せないので、手前で分岐する。
      */}
      {renderResult()}
    </section>
  );
}
