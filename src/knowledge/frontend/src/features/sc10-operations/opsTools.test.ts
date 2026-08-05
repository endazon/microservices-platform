import { describe, it, expect } from 'vitest';
import { i18n } from '@foundation/i18n';
import { opsTools, OPS_TOOL_IDS } from './opsTools';

// SC-10, FR-10: 外部可観測性ツールの導線（純関数）。
//
// **値集合を純関数テストで固定する**理由（IADR-0129 決定 6）: 画面テストは実行時 config を
// 与えて描画結果を見るだけなので、**定義から 1 件落ちても「その config を渡していないだけ」と
// 区別が付かない**（#503 の変異試験 M31 と同型）。

describe('opsTools (SC-10)', () => {
  // 計画 §SC-10 §主要素: Grafana＝メトリクス・コスト／Kiali＝サービスメッシュ／
  // Jaeger・Tempo＝分散トレース の 3 件。
  it('fixes exactly the three tools the plan lists', () => {
    expect([...OPS_TOOL_IDS]).toEqual(['grafana', 'kiali', 'tracing']);
  });

  it('builds all three links in the order the plan lists them', () => {
    const tools = opsTools({
      grafanaUrl: 'https://grafana.example',
      kialiUrl: 'https://kiali.example',
      jaegerUrl: 'https://jaeger.example',
    });
    expect(tools.map((t) => t.id)).toEqual(['grafana', 'kiali', 'tracing']);
    expect(tools.map((t) => t.name)).toEqual(['Grafana', 'Kiali', 'Jaeger / Tempo']);
    expect(tools.map((t) => i18n._(t.description))).toEqual([
      'メトリクス・コスト',
      'サービスメッシュ',
      '分散トレース',
    ]);
    expect(tools.map((t) => t.url)).toEqual([
      'https://grafana.example',
      'https://kiali.example',
      'https://jaeger.example',
    ]);
  });

  // 未設定のツールは導線を出さない（Kiali 未配備などを想定した既存の設計）。
  it('drops tools whose URL is not injected at runtime', () => {
    const tools = opsTools({ grafanaUrl: 'https://grafana.example' });
    expect(tools.map((t) => t.id)).toEqual(['grafana']);
  });

  it('returns nothing when no tool is configured', () => {
    expect(opsTools({})).toEqual([]);
  });
});
