import type {
  EdgeTypeCatalogItem,
  GraphEdgeItem,
  GraphNodeItem,
} from '@foundation/api/generated/bff.schemas';

// SC-18, UC-01, FR-17, ADR-0039 (#917): グラフ描画 option の組み立て（純関数）。
//
// **option の組み立てを描画（echarts.init）から切り出す**のは SC-10 の dashboardCharts と
// 同じ理由である —— jsdom には実描画が無く、「どのノードがどの形で出るか」「どの辺が
// どの線種か」という判断の部分はここでしかテストできない。
//
// **表示文言はここに書かない。** 呼び出し側が翻訳済みの文字列を渡す（Lingui の抽出対象から
// 外さない。IADR-0125 決定 4）。
//
// 🔴 **色だけで意味を持たせない**（05_screens §SC-18・利用者裁定）。区別はすべて
// 形（円 / 角丸四角）・アイコン（📄 / 👤）・線種（実線 / 破線 / 点線）・太さ・矢印が担い、
// 色は補助である。系列色は既定に任せ、ノード・辺の意味は凡例（GraphLegend）のテキストが持つ。

export type ChartOption = Record<string, unknown>;

/** 辺の描き分け（05_screens §SC-18 の表そのまま）。 */
export interface EdgeVisual {
  lineType: 'solid' | 'dashed' | 'dotted';
  width: number;
  /** 有向なら矢印を出す（`related` は無向）。 */
  arrow: boolean;
}

// 中核 5 種の描き分け表（05_screens §SC-18。型は SC-09 の辞書が正であり、ここでは
// **表示名で引く** —— 応答の辺は `edgeTypeId` しか持たず、名前は辞書側で解決する。ADR-0033 決定 9）。
const CORE_EDGE_VISUALS: Record<string, EdgeVisual> = {
  related: { lineType: 'solid', width: 1, arrow: false },
  cites: { lineType: 'solid', width: 2, arrow: true },
  supersedes: { lineType: 'solid', width: 4, arrow: true },
  'derived-from': { lineType: 'dashed', width: 2, arrow: true },
  embeds: { lineType: 'dotted', width: 2, arrow: true },
};

/**
 * 型の描き分けを引く。**未定義の型は `related` 相当へ縮退**し、向きだけは辞書の
 * `isSymmetric` に従う（辞書は SC-09 で増えるため、未知の型で描画を壊さない）。
 */
export function edgeVisualFor(typeName: string | undefined, isSymmetric: boolean): EdgeVisual {
  const known = typeName ? CORE_EDGE_VISUALS[typeName] : undefined;
  if (known) return known;
  return { lineType: 'solid', width: 1, arrow: !isSymmetric };
}

/** 表示中の辺を 1 本も持たないノード（孤立文書。SC-18 のハイライト対象）。 */
export function isolatedNodeIds(
  nodes: readonly GraphNodeItem[],
  edges: readonly GraphEdgeItem[],
): Set<string> {
  const connected = new Set<string>();
  for (const e of edges) {
    connected.add(e.sourceDocumentId);
    connected.add(e.targetDocumentId);
  }
  return new Set(nodes.filter((n) => !connected.has(n.documentId)).map((n) => n.documentId));
}

export interface GraphOptionInput {
  nodes: readonly GraphNodeItem[];
  edges: readonly GraphEdgeItem[];
  /** 辺の型辞書（描き分け・向きの解決に使う）。 */
  edgeTypes: readonly EdgeTypeCatalogItem[];
  /** 起点ノード（強調表示。SC-18 主要素 2）。 */
  originId?: string;
  /** グラフ内検索でフォーカス中のノード。 */
  focusedId?: string;
  /** 翻訳済みの文言（ツールチップの種別ラベル）。 */
  labels: { organization: string; privateNote: string; isolated: string };
}

/** ツールチップへ渡すためにノードごとの意味を畳み込んだデータ行。 */
interface GraphNodeDatum {
  id: string;
  name: string;
  scopeLabel: string;
  isolatedLabel: string | null;
  [key: string]: unknown;
}

/**
 * SC-18 のグラフ option を組む。
 *
 * - 組織文書＝**円**＋📄 / 個人資料＝**角丸四角**＋👤・破線輪郭（利用者裁定・質問票 第11回 Q3）。
 *   アイコンはノード内ラベルとして描く（200 ノードでタイトルの常時表示は潰れるため、
 *   タイトルはホバーのツールチップが出す）。
 * - 起点は太い輪郭＋大きめのサイズで他ノードと区別する。
 * - 孤立文書（表示中の辺 0 本）は点線輪郭＋薄い塗りでハイライトし、ツールチップにも明記する。
 * - AI 提案由来（provenance = ai-approved）の辺は**型を問わず破線**にする（05_screens §SC-18。
 *   `approved` のみが応答に載ることは探索側が保証する。ADR-0033 決定 7）。
 */
export function buildGraphOption(input: GraphOptionInput): ChartOption {
  const typeById = new Map(input.edgeTypes.map((t) => [t.id, t]));
  const isolated = isolatedNodeIds(input.nodes, input.edges);

  const data: GraphNodeDatum[] = input.nodes.map((n) => {
    const isOrigin = n.documentId === input.originId;
    const isFocused = n.documentId === input.focusedId;
    const isIsolated = isolated.has(n.documentId);
    const isPrivate = n.isPrivateNote === true;
    return {
      id: n.documentId,
      name: n.title,
      scopeLabel: isPrivate ? input.labels.privateNote : input.labels.organization,
      isolatedLabel: isIsolated ? input.labels.isolated : null,
      symbol: isPrivate ? 'roundRect' : 'circle',
      symbolSize: isOrigin ? 34 : isFocused ? 26 : 16,
      label: {
        show: true,
        position: 'inside',
        formatter: isPrivate ? '👤' : '📄',
        fontSize: isOrigin ? 14 : 9,
      },
      itemStyle: {
        // 形＋アイコンが第 1 の手掛かり。輪郭は起点（太実線）/ 個人資料（破線）/ 孤立（点線）を重ねる。
        borderWidth: isOrigin ? 4 : isPrivate || isIsolated || isFocused ? 2 : 1,
        borderType: isIsolated ? 'dotted' : isPrivate ? 'dashed' : 'solid',
        opacity: isIsolated ? 0.75 : 1,
      },
    };
  });

  const links = input.edges.map((e) => {
    const type = typeById.get(e.edgeTypeId);
    const visual = edgeVisualFor(type?.name, type?.isSymmetric ?? true);
    // AI 提案由来は破線で示す（確定した辺との区別。色に頼らない）。
    const lineType = e.provenance === 'ai-approved' ? 'dashed' : visual.lineType;
    return {
      source: e.sourceDocumentId,
      target: e.targetDocumentId,
      lineStyle: { type: lineType, width: visual.width },
      symbol: visual.arrow ? ['none', 'arrow'] : ['none', 'none'],
      symbolSize: 8,
    };
  });

  return {
    animation: false,
    tooltip: {
      // ノード: タイトル＋種別（＋孤立の注記）。ホバーでラベルを出す（SC-18・モックアップ準拠）。
      formatter: (params: { dataType?: string; data?: GraphNodeDatum }) => {
        if (params.dataType !== 'node' || !params.data) return '';
        const d = params.data;
        const isolatedNote = d.isolatedLabel ? `<br/>${d.isolatedLabel}` : '';
        return `<b>${escapeHtml(d.name)}</b><br/>${escapeHtml(d.scopeLabel)}${isolatedNote}`;
      },
    },
    series: [
      {
        type: 'graph',
        layout: 'force',
        roam: true,
        data,
        links,
        force: { repulsion: 80, gravity: 0.1, edgeLength: 50 },
        emphasis: { focus: 'adjacency' },
        selectedMode: 'single',
        select: { itemStyle: { borderWidth: 4 } },
      },
    ],
  };
}

/** ツールチップは HTML として描画されるため、タイトル（利用者入力由来）を必ずエスケープする。 */
function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}
