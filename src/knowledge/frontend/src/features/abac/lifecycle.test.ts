import { describe, it, expect } from 'vitest';
import { DEFAULT_LIFECYCLE, LIFECYCLE_KEY, LIFECYCLE_VALUES } from './lifecycle';

// FR-05, UC-04, SC-06: ライフサイクル状態（ABAC 文書属性）の語彙。
//
// 3 つとも**後段・計画と一致していなければ意味を失う文字列**である。キーがずれれば属性辞書の別の
// キーへ書き込まれ（後段はフェイルセーフで `active` を入れるので、**画面上は何も起きずに管理者の
// 指定だけが消える**）、値がずれれば ABAC ポリシーの `allowedLifecycles` に一致せず文書が到達不能になり、
// 終端値がずれれば画面の説明が嘘になる。画面テスト経由の間接被覆では文字列そのものを固定できない
// ため、ここで直接固定する。

describe('lifecycle (FR-05, UC-04, SC-06)', () => {
  // バックエンド `DataSource.LifecycleKey` と同じ値である。
  it('names the attribute key the ABAC dictionary uses', () => {
    expect(LIFECYCLE_KEY).toBe('lifecycle');
  });

  // 計画 07_abac-attribute-model §文書の基本属性の 3 値。05_screens §SC-05 が
  // 「状態の語彙はこれを正とする」と再確認している。
  it('mirrors the three states the plan defines', () => {
    expect(LIFECYCLE_VALUES).toEqual(['draft', 'active', 'archived']);
  });

  // 計画が名指しで「計画側の語彙ではない」と書いた 2 語を、実装が持ち込んでいないこと。
  // 端点名（`publish` / `archive`）は動詞であり、属性値ではない。
  it('does not introduce states the plan does not have', () => {
    for (const notAState of ['normalized', 'published', 'publish', 'archive']) {
      expect(LIFECYCLE_VALUES as readonly string[]).not.toContain(notAState);
    }
  });

  // 計画 09_datasource-connectors §システム投入経路の終端。バックエンド
  // `DataSource.DefaultLifecycle` と同じ値である（裁定 planning#361。IADR-0199 決定 4）。
  it('mirrors the terminal default the backend falls back to', () => {
    expect(DEFAULT_LIFECYCLE).toBe('active');
    expect(LIFECYCLE_VALUES as readonly string[]).toContain(DEFAULT_LIFECYCLE);
  });
});
