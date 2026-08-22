import { describe, it, expect } from 'vitest';
import { i18n } from '@foundation/i18n';
import { buildAnalysisRequest, TASK_TYPES, taskTypeLabel } from './analysisRange';

// SC-08, UC-02, FR-07: 分析要求の組み立て（純関数）。

describe('analysisRange (SC-08)', () => {
  // FR-07「AI に対し、指定データ範囲での**分析・比較・抽出**を依頼できる」。
  it('offers the three task types the requirement names', () => {
    expect([...TASK_TYPES]).toEqual(['Analyze', 'Compare', 'Extract']);
    expect(i18n._(taskTypeLabel('Analyze'))).toBe('分析');
    expect(i18n._(taskTypeLabel('Compare'))).toBe('比較');
    expect(i18n._(taskTypeLabel('Extract'))).toBe('抽出');
  });

  // 空の `range` を送ると「範囲を指定した」ことになり、サーバの解釈（省略時は instruction を流用）と
  // 食い違う。検索条件が無いときは range そのものを付けない。
  it('omits the range entirely when no search condition is given', () => {
    expect(buildAnalysisRequest('  比較して  ', 'Compare', '   ')).toEqual({
      instruction: '比較して',
      taskType: 'Compare',
    });
  });

  it('adds the range query when a search condition is given', () => {
    expect(buildAnalysisRequest('比較して', 'Compare', '  Q1 営業報告 ')).toEqual({
      instruction: '比較して',
      taskType: 'Compare',
      range: { query: 'Q1 営業報告' },
    });
  });

  // FR-05: クライアントは ABAC スコープを送らない（送っても BFF は使わない＝権限昇格の防止）。
  it('never sends an access scope or attribute filters from the client', () => {
    const request = buildAnalysisRequest('比較して', 'Analyze', 'Q1');
    expect(Object.keys(request)).toEqual(['instruction', 'taskType', 'range']);
    expect(Object.keys(request.range ?? {})).toEqual(['query']);
  });

  // **［#788］送信可否の検証は `analysisFormSchema.test.ts` へ移った。**
  // 規則の実体が Zod スキーマ（ADR-0031 §採用技術一覧 のフォーム基盤）へ移ったためであり、
  // 検証が消えたわけではない（同じ 5 条件を符号つきで固定している）。
});
