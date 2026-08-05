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
