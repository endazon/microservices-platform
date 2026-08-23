import { describe, it, expect } from 'vitest';
import type {
  EdgeTypeCatalogItem,
  GraphEdgeItem,
  GraphNodeItem,
} from '@foundation/api/generated/bff.schemas';
import { buildGraphOption, edgeVisualFor, isolatedNodeIds } from './graphOption';

// SC-18, FR-17, UC-10: グラフ option の組み立て（描き分けの判断部分）。
// 形（円 / 角丸四角）・アイコン・線種・矢印の写像は jsdom の描画からは検証できないため、
// 純関数の出力で固定する（dashboardCharts.test と同じ構図）。

const LABELS = {
  organization: '組織文書',
  privateNote: '個人資料（自分のみ）',
  isolated: '孤立文書',
};

const TYPE_RELATED: EdgeTypeCatalogItem = {
  id: '11111111-1111-1111-1111-111111111111',
  name: 'related',
  layer: 'core',
  isSymmetric: true,
};
const TYPE_CITES: EdgeTypeCatalogItem = {
  id: '22222222-2222-2222-2222-222222222222',
  name: 'cites',
  layer: 'core',
  isSymmetric: false,
};
const TYPE_SUPERSEDES: EdgeTypeCatalogItem = {
  id: '33333333-3333-3333-3333-333333333333',
  name: 'supersedes',
  layer: 'core',
  isSymmetric: false,
};

function node(id: string, title: string, isPrivateNote = false): GraphNodeItem {
  return { documentId: id, title, isPrivateNote };
}

function edge(
  id: string,
  source: string,
  target: string,
  typeId: string,
  provenance: GraphEdgeItem['provenance'] = 'auto',
): GraphEdgeItem {
  return { id, sourceDocumentId: source, targetDocumentId: target, edgeTypeId: typeId, provenance };
}

type SeriesDatum = {
  id: string;
  symbol: string;
  symbolSize: number;
  itemStyle: { borderType: string; borderWidth: number; opacity: number };
  label: { formatter: string };
};
type SeriesLink = { lineStyle: { type: string; width: number }; symbol: [string, string] };

function series(option: Record<string, unknown>) {
  const s = (option.series as Array<Record<string, unknown>>)[0];
  return { data: s.data as SeriesDatum[], links: s.links as SeriesLink[] };
}

describe('edgeVisualFor (SC-18 辺の型の描き分け表)', () => {
  // 05_screens §SC-18 の表そのまま: 中核 5 種を線種・太さ・矢印で区別する（色だけにしない）。
  it('maps the five core types to the fixed visual table', () => {
    expect(edgeVisualFor('related', true)).toEqual({ lineType: 'solid', width: 1, arrow: false });
    expect(edgeVisualFor('cites', false)).toEqual({ lineType: 'solid', width: 2, arrow: true });
    expect(edgeVisualFor('supersedes', false)).toEqual({
      lineType: 'solid',
      width: 4,
      arrow: true,
    });
    expect(edgeVisualFor('derived-from', false)).toEqual({
      lineType: 'dashed',
      width: 2,
      arrow: true,
    });
    expect(edgeVisualFor('embeds', false)).toEqual({ lineType: 'dotted', width: 2, arrow: true });
  });

  // 辞書は SC-09 で増える。未知の型は related 相当へ縮退し、**向きだけは辞書の isSymmetric に従う**。
  it('falls back to a thin solid line for unknown types and keeps the direction', () => {
    expect(edgeVisualFor('implements', false)).toEqual({
      lineType: 'solid',
      width: 1,
      arrow: true,
    });
    expect(edgeVisualFor(undefined, true)).toEqual({ lineType: 'solid', width: 1, arrow: false });
  });
});

describe('isolatedNodeIds (SC-18 孤立文書ハイライト)', () => {
  it('collects nodes that no displayed edge touches', () => {
    const nodes = [node('a', 'A'), node('b', 'B'), node('c', 'C')];
    const edges = [edge('e1', 'a', 'b', TYPE_RELATED.id)];
    expect(isolatedNodeIds(nodes, edges)).toEqual(new Set(['c']));
  });

  // 陽性対照: 辺の両端は孤立ではない（「全ノードを孤立扱いする実装」を落とす）。
  it('does not mark connected nodes', () => {
    const nodes = [node('a', 'A'), node('b', 'B')];
    const edges = [edge('e1', 'a', 'b', TYPE_RELATED.id)];
    expect(isolatedNodeIds(nodes, edges).size).toBe(0);
  });
});

describe('buildGraphOption (SC-18)', () => {
  const catalog = [TYPE_RELATED, TYPE_CITES, TYPE_SUPERSEDES];

  // 利用者裁定（質問票 第11回 Q3）: 組織文書＝円＋📄 / 個人資料＝角丸四角＋👤・破線輪郭。
  // **色だけで区別しない** —— 形とアイコンが出力に現れることを固定する。
  it('draws organization documents as circles and private notes as dashed round rects', () => {
    const option = buildGraphOption({
      nodes: [node('org', '組織の文書'), node('priv', '自分のメモ', true)],
      edges: [edge('e1', 'org', 'priv', TYPE_RELATED.id)],
      edgeTypes: catalog,
      labels: LABELS,
    });
    const { data } = series(option);
    const org = data.find((d) => d.id === 'org')!;
    const priv = data.find((d) => d.id === 'priv')!;
    expect(org.symbol).toBe('circle');
    expect(org.label.formatter).toBe('📄');
    expect(org.itemStyle.borderType).toBe('solid');
    expect(priv.symbol).toBe('roundRect');
    expect(priv.label.formatter).toBe('👤');
    expect(priv.itemStyle.borderType).toBe('dashed');
  });

  // SC-18 主要素 2: 起点は他ノードと明確に区別できる強調表示。
  it('emphasises the origin node with size and border', () => {
    const option = buildGraphOption({
      nodes: [node('root', '起点'), node('leaf', '隣')],
      edges: [edge('e1', 'root', 'leaf', TYPE_CITES.id)],
      edgeTypes: catalog,
      originId: 'root',
      labels: LABELS,
    });
    const { data } = series(option);
    const root = data.find((d) => d.id === 'root')!;
    const leaf = data.find((d) => d.id === 'leaf')!;
    expect(root.symbolSize).toBeGreaterThan(leaf.symbolSize);
    expect(root.itemStyle.borderWidth).toBeGreaterThan(leaf.itemStyle.borderWidth);
  });

  // SC-18: 孤立文書は点線輪郭でハイライトする（形の区別〔破線＝個人資料〕とは別の線種）。
  it('highlights isolated nodes with a dotted border', () => {
    const option = buildGraphOption({
      nodes: [node('a', 'A'), node('b', 'B'), node('alone', '孤立')],
      edges: [edge('e1', 'a', 'b', TYPE_RELATED.id)],
      edgeTypes: catalog,
      labels: LABELS,
    });
    const { data } = series(option);
    expect(data.find((d) => d.id === 'alone')!.itemStyle.borderType).toBe('dotted');
    expect(data.find((d) => d.id === 'a')!.itemStyle.borderType).toBe('solid');
  });

  // 辺の写像: 型の描き分け＋向き（related は矢印なし・cites は矢印）。
  it('maps edge visuals from the type catalog', () => {
    const option = buildGraphOption({
      nodes: [node('a', 'A'), node('b', 'B')],
      edges: [edge('e1', 'a', 'b', TYPE_RELATED.id), edge('e2', 'a', 'b', TYPE_SUPERSEDES.id)],
      edgeTypes: catalog,
      labels: LABELS,
    });
    const { links } = series(option);
    expect(links[0].lineStyle).toEqual({ type: 'solid', width: 1 });
    expect(links[0].symbol).toEqual(['none', 'none']);
    expect(links[1].lineStyle).toEqual({ type: 'solid', width: 4 });
    expect(links[1].symbol).toEqual(['none', 'arrow']);
  });

  // ADR-0033 決定 7 / SC-18: AI 提案由来（approved のみが応答に載る）は**型を問わず破線**。
  it('draws ai-approved edges dashed regardless of type', () => {
    const option = buildGraphOption({
      nodes: [node('a', 'A'), node('b', 'B')],
      edges: [edge('e1', 'a', 'b', TYPE_CITES.id, 'ai-approved')],
      edgeTypes: catalog,
      labels: LABELS,
    });
    const { links } = series(option);
    expect(links[0].lineStyle.type).toBe('dashed');
  });

  // 陽性対照（上の対）: 確定した辺（auto / user）は型の線種のまま。
  it('keeps confirmed edges on the type visual', () => {
    const option = buildGraphOption({
      nodes: [node('a', 'A'), node('b', 'B')],
      edges: [edge('e1', 'a', 'b', TYPE_CITES.id, 'user')],
      edgeTypes: catalog,
      labels: LABELS,
    });
    expect(series(option).links[0].lineStyle.type).toBe('solid');
  });

  // ツールチップはタイトル（利用者入力由来）を HTML として描くため、エスケープを固定する。
  it('escapes node titles in the tooltip', () => {
    const option = buildGraphOption({
      nodes: [node('x', '<img src=x onerror=alert(1)>')],
      edges: [],
      edgeTypes: catalog,
      labels: LABELS,
    });
    const tooltip = option.tooltip as {
      formatter: (p: { dataType?: string; data?: unknown }) => string;
    };
    const html = tooltip.formatter({
      dataType: 'node',
      data: series(option).data[0],
    });
    expect(html).not.toContain('<img');
    expect(html).toContain('&lt;img');
    // 種別ラベル（翻訳済みで渡されたもの）も併記される（ホバーでラベル表示。SC-18）。
    expect(html).toContain('組織文書');
  });
});
