import { describe, it, expect } from 'vitest';
import { DEPARTMENT_KEY, UNRESOLVED_DEPARTMENT } from './department';

// FR-05, UC-04, SC-06: 所管部門（ABAC 文書属性）の語彙。
//
// 2 つとも**後段と一致していなければ意味を失う文字列**である。キーがずれれば属性辞書の別の
// キーへ書き込まれ（後段はフェイルセーフで `unassigned` を入れるので、**画面上は何も起きずに
// 管理者の入力だけが消える**）、予約値がずれれば画面の説明が嘘になる。画面テスト経由の間接
// 被覆では文字列そのものを固定できないため、ここで直接固定する。

describe('department (FR-05, UC-04, SC-06)', () => {
  // バックエンド `DataSource.DepartmentKey` と同じ値である。
  it('names the attribute key the ABAC dictionary uses', () => {
    expect(DEPARTMENT_KEY).toBe('department');
  });

  // 計画 09_datasource-connectors §システム投入経路の 3 段目。バックエンド
  // `DataSource.UnresolvedDepartment` と同じ値である（IADR-0199）。
  it('mirrors the reserved value the backend falls back to', () => {
    expect(UNRESOLVED_DEPARTMENT).toBe('unassigned');
  });
});
