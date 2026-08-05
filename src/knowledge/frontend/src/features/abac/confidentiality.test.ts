import { describe, it, expect } from 'vitest';
import {
  CONFIDENTIALITY_KEY,
  CONFIDENTIALITY_VALUES,
  DEFAULT_CONFIDENTIALITY,
} from './confidentiality';

// FR-05/FR-09, SC-05/SC-06: 機密区分（ABAC 文書属性）の値集合。
//
// 値集合は ABAC の一次情報（計画 06_technical/07_abac-attribute-model の 4 値）に由来する。
// **勝手に増減すると機密区分の取り違えに直結する**——値が減れば選べない区分が生まれ、
// 増えれば後段が知らない区分で保存される。画面テスト経由の間接被覆では
// 「4 値であること」そのものは固定できないため、ここで直接固定する。

describe('confidentiality (FR-05/FR-09, SC-05/SC-06)', () => {
  it('fixes exactly the four values of the ABAC attribute dictionary', () => {
    expect([...CONFIDENTIALITY_VALUES]).toEqual([
      'public',
      'internal',
      'confidential',
      'restricted',
    ]);
  });

  // IADR-0019: 既定は public（過剰公開）でも restricted（過剰制限）でもなく internal である。
  it('uses internal as the fail-safe default', () => {
    expect(DEFAULT_CONFIDENTIALITY).toBe('internal');
    expect(CONFIDENTIALITY_VALUES).toContain(DEFAULT_CONFIDENTIALITY);
  });

  // 属性辞書のキー。SC-05 の一覧・フォームと SC-06 の既定属性が同じキーで読み書きする。
  it('names the attribute key the ABAC dictionary uses', () => {
    expect(CONFIDENTIALITY_KEY).toBe('confidentiality');
  });
});
