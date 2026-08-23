import { describe, it, expect } from 'vitest';
import { analysisFormSchema } from './analysisFormSchema';
import { MAX_INSTRUCTION_LENGTH, MAX_RANGE_QUERY_LENGTH } from './analysisRange';

// SC-08, UC-02, FR-07 / ADR-0031 §採用技術一覧（フォーム = RHF + Zod）/ #788:
// 検証規則を純粋なスキーマとして固定する（描画を通さずに規則の退行を捕まえる）。

const base = { instruction: '売上を比較して', taskType: 'Analyze' as const, rangeQuery: '' };

describe('analysisFormSchema', () => {
  it('accepts a valid input', () => {
    expect(analysisFormSchema.safeParse(base).success).toBe(true);
  });

  // SC-08: 空白だけの指示は送らない（サーバへ意味の無い要求を出さない）。
  it('rejects a blank instruction with the "required" code', () => {
    const result = analysisFormSchema.safeParse({ ...base, instruction: '   ' });
    expect(result.success).toBe(false);
    expect(result.error?.issues[0].message).toBe('required');
  });

  // SC-08: 上限超過はサーバが 400 を返すため手前で止める（上限の正本は analysisRange.ts）。
  it('rejects an over-long instruction with the "tooLong" code', () => {
    const result = analysisFormSchema.safeParse({
      ...base,
      instruction: 'あ'.repeat(MAX_INSTRUCTION_LENGTH + 1),
    });
    expect(result.success).toBe(false);
    expect(result.error?.issues[0].message).toBe('tooLong');
  });

  it('rejects an over-long range query', () => {
    const result = analysisFormSchema.safeParse({
      ...base,
      rangeQuery: 'あ'.repeat(MAX_RANGE_QUERY_LENGTH + 1),
    });
    expect(result.success).toBe(false);
    expect(result.error?.issues[0].message).toBe('tooLong');
  });

  // FR-07「分析・比較・抽出」: 契約の 3 値以外は受け付けない。
  it('rejects an unknown task type', () => {
    expect(analysisFormSchema.safeParse({ ...base, taskType: 'Summarize' }).success).toBe(false);
  });

  // #788: 文言はスキーマに持たせない（Lingui の抽出対象から外れ、網羅検査を素通りするため）。
  it('carries stable codes, not display text', () => {
    const result = analysisFormSchema.safeParse({ ...base, instruction: '' });
    expect(result.error?.issues[0].message).toMatch(/^[a-zA-Z]+$/);
  });
});
