import { describe, it, expect } from 'vitest';
import { UNRESOLVED_OWNER } from './owner';

// FR-05, UC-04, SC-06, ADR-0074: 所有者（ABAC 文書属性）の語彙。
//
// `department` と同じく、**後段と一致していなければ意味を失う文字列**である。キーがずれれば
// 属性辞書の別のキーを指し、予約値がずれれば画面の説明が嘘になる。画面テスト経由の間接被覆では
// 文字列そのものを固定できないため、ここで直接固定する。

describe('owner (FR-05, UC-04, SC-06)', () => {
  // 計画 09_datasource-connectors §システム投入経路の終端。バックエンド
  // `DataSource.UnresolvedOwner` と同じ値である（IADR-0199 / ADR-0074 決定 3）。
  it('mirrors the reserved value the backend falls back to', () => {
    expect(UNRESOLVED_OWNER).toBe('system');
  });
});
