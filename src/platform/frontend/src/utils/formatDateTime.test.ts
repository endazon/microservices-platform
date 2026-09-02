import { describe, it, expect } from 'vitest';
import { formatDateTime } from './formatDateTime';

// ADR-0031 §採用技術一覧（日付 = dayjs）/ IADR-0121 決定 1 第 4 段（#788）:
// dayjs へ載せ替えても**契約（`—` / 原文素通し）は変えていない**ことと、
// 旧実装で固定できなかった「整形結果そのもの」を、ここで初めて固定する。

describe('formatDateTime', () => {
  // SC-02 / SC-06: 値が無い 3 系統は同じ `—` へ寄せる（索引の内部事情を画面へ出さない。IADR-0149 決定 3）。
  it('renders an em dash for missing values', () => {
    expect(formatDateTime(null)).toBe('—');
    expect(formatDateTime(undefined)).toBe('—');
    expect(formatDateTime('')).toBe('—');
  });

  // SC-06: 解釈できない値は握り潰さず原文を出す（壊れた値が届いていることを見えなくしない）。
  it('passes through unparsable values', () => {
    expect(formatDateTime('not-a-date')).toBe('not-a-date');
  });

  // #788: 表記は固定書式。ロケールに依存しないことを固定する
  //（依存していると、同じ値が実行環境ごとに違う文字列になり退行を検出できない）。
  it('formats with a fixed pattern', () => {
    // ローカル時刻へ落ちるため、桁の形だけを固定する（実行環境の TZ に結果を依存させない）。
    expect(formatDateTime('2026-08-01T03:04:05Z')).toMatch(/^\d{4}\/\d{2}\/\d{2} \d{2}:\d{2}$/);
  });

  // #788: 秒は出さない（列幅だけを食い、読み手の判断を変えない）。
  it('omits seconds', () => {
    expect(formatDateTime('2026-08-01T03:04:05Z')).not.toMatch(/:\d{2}:\d{2}$/);
  });
});
