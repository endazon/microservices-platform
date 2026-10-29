import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';
import type { OpsLinks } from '@foundation/config/runtimeConfig';

// SC-10, FR-10: 外部可観測性ツールへの導線（純関数）。
//
// 計画 05_screens §SC-10 §主要素:「外部ツールリンク（Grafana＝メトリクス・コスト／
// Kiali＝サービスメッシュ／Jaeger・Tempo＝分散トレース）」。
//
// 接続先はビルドへ焼き込まず実行時 config（platform/frontend/public/config.js）で注入する
// （IADR-0121 決定 3）。**未設定のツールは導線を出さない**（Kiali 未配備などを想定した既存の設計）。
//
// 判定を DOM から切り離してあるのは、**3 件という値集合と絞り込みの規則そのもの**を
// 描画なしで試験できるようにするためである（IADR-0129 決定 6）。

/** 計画が挙げる外部ツールの 3 件。 */
export const OPS_TOOL_IDS = ['grafana', 'kiali', 'tracing'] as const;

export type OpsToolId = (typeof OPS_TOOL_IDS)[number];

export interface OpsTool {
  id: OpsToolId;
  /** 製品の固有名詞。**翻訳しない**（IADR-0129 / 05_screens §共通シェル のブランド表示名と同じ扱い）。 */
  name: string;
  /** 役割の説明。計画 §主要素 の記述に由来するため**翻訳する**。 */
  description: MessageDescriptor;
  url: string;
}

/** 各ツールの表示名・説明と、実行時 config のどの項目を読むか。 */
const DEFINITIONS: readonly {
  id: OpsToolId;
  name: string;
  description: MessageDescriptor;
  pick: (links: OpsLinks) => string | undefined;
}[] = [
  {
    id: 'grafana',
    name: 'Grafana',
    description: msg`メトリクス・コスト`,
    pick: (links) => links.grafanaUrl,
  },
  {
    id: 'kiali',
    name: 'Kiali',
    description: msg`サービスメッシュ`,
    pick: (links) => links.kialiUrl,
  },
  {
    id: 'tracing',
    name: 'Jaeger / Tempo',
    description: msg`分散トレース`,
    pick: (links) => links.jaegerUrl,
  },
];

/**
 * 実行時 config から導線を組み立てる。**URL が未設定のツールは落とす**。
 *
 * 並び順は計画 §主要素 の記述順（Grafana → Kiali → Jaeger・Tempo）に固定する。
 */
export function opsTools(links: OpsLinks): OpsTool[] {
  return DEFINITIONS.flatMap(({ id, name, description, pick }) => {
    const url = pick(links);
    return url ? [{ id, name, description, url }] : [];
  });
}

// FR-10, SC-10 (#2125): SLO・レイテンシと LLM コストの導線カード。
//
// 計画 SC-10 §主要素「SLO・レイテンシと LLM コストは Grafana への導線カード」。値は出さない ——
// BFF が再集計すると同じ指標の定義が 2 か所に生まれる（KPI カードの確定 Q25 / Q26）。
// 送り先は Grafana の provisioning が持つダッシュボードの uid である
// （`deploy/grafana/provisioning/dashboards/*.json` と `deploy/local/observability/grafana.yaml` の両方に同じ uid がある）。
//   - SLO・レイテンシ → `microservices-platform-overview`（HTTP の p99・RAG の初回応答 p95 など）
//   - LLM コスト → `llm-usage`（LLM ゲートウェイのトークン使用量）

type GrafanaGuideId = 'slo' | 'llm-cost';

interface GrafanaGuide {
  id: GrafanaGuideId;
  /** カードの指標名（hi-fi 419・421 の `.stat .l`）。 */
  label: MessageDescriptor;
  /** 補足（値の正がどこにあるか）。 */
  note: MessageDescriptor;
  /** Grafana の該当ダッシュボード。接続先が未設定なら `undefined`（カードは残し、リンクだけを持たない）。 */
  url: string | undefined;
}

const GUIDES: readonly {
  id: GrafanaGuideId;
  label: MessageDescriptor;
  note: MessageDescriptor;
  uid: string;
}[] = [
  {
    id: 'slo',
    label: msg`SLO・レイテンシ`,
    note: msg`達成率・レイテンシは可観測性基盤が正`,
    uid: 'microservices-platform-overview',
  },
  {
    id: 'llm-cost',
    label: msg`LLMコスト`,
    note: msg`使用量・費用は可観測性基盤が担う`,
    uid: 'llm-usage',
  },
];

/** Grafana の導線カード 2 枚を組み立てる。並びは hi-fi の左から（SLO → LLM コスト）。 */
export function grafanaGuides(grafanaUrl: string | undefined): GrafanaGuide[] {
  const base = grafanaUrl?.replace(/\/+$/, '');
  return GUIDES.map(({ id, label, note, uid }) => ({
    id,
    label,
    note,
    url: base ? `${base}/d/${uid}` : undefined,
  }));
}
