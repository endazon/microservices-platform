import { describe, it, expect } from 'vitest';
import type { BreakGlassAuditEntryDto } from '@foundation/api/generated/bff.schemas';
import {
  REASON_MAX_LENGTH,
  auditOperationKeyOf,
  auditUsernamesOf,
  canOfferView,
  endedGrantIdsOf,
  reasonStateOf,
  remainingSeconds,
  sameUser,
  splitMinutesSeconds,
} from './breakGlass';

// SC-19, SC-03, FR-19, 計画 ADR-0036 D-08, planning#841 / [[IADR-0612]] (#2046): 緊急アクセスの画面の純関数。

const NOW = Date.parse('2026-10-24T03:00:00Z');

function entry(overrides: Partial<BreakGlassAuditEntryDto> = {}): BreakGlassAuditEntryDto {
  return {
    id: 'a',
    grantId: 'g',
    occurredAt: '2026-10-24T02:57:00Z',
    operation: 'activate',
    actorUserId: 'owner',
    documentId: 'doc',
    ownerUserId: 'owner',
    nomineeUserId: 'admin',
    reason: 'r',
    activatedAt: '2026-10-24T02:57:00Z',
    endsAt: '2026-10-24T03:12:00Z',
    viewTarget: null,
    ...overrides,
  };
}

describe('reasonStateOf（理由は必須・500 文字以内）', () => {
  it.each([
    ['', 'empty'],
    ['   \n\t', 'empty'],
    ['理由', 'ok'],
    ['あ'.repeat(REASON_MAX_LENGTH), 'ok'],
    ['あ'.repeat(REASON_MAX_LENGTH + 1), 'tooLong'],
    // 前後の空白は数えない（送るのは trim した値）。
    [`  ${'あ'.repeat(REASON_MAX_LENGTH)}  `, 'ok'],
  ])('%j → %s', (reason, expected) => {
    expect(reasonStateOf(reason)).toBe(expected);
  });

  it('上限は後段と同じ 500', () => {
    expect(REASON_MAX_LENGTH).toBe(500);
  });
});

describe('remainingSeconds / splitMinutesSeconds', () => {
  it('終了予定までの秒数（過ぎたら 0。負にしない）', () => {
    expect(remainingSeconds('2026-10-24T03:12:00Z', NOW)).toBe(720);
    expect(remainingSeconds('2026-10-24T03:00:00Z', NOW)).toBe(0);
    expect(remainingSeconds('2026-10-24T02:59:00Z', NOW)).toBe(0);
    expect(remainingSeconds('not-a-date', NOW)).toBe(0);
  });

  it('分と秒へ分ける', () => {
    expect(splitMinutesSeconds(720)).toEqual({ minutes: 12, seconds: 0 });
    expect(splitMinutesSeconds(61)).toEqual({ minutes: 1, seconds: 1 });
    expect(splitMinutesSeconds(0)).toEqual({ minutes: 0, seconds: 0 });
  });
});

describe('auditOperationKeyOf', () => {
  it('契約の 3 値はそのまま、知らない値は unknown', () => {
    expect(auditOperationKeyOf('activate')).toBe('activate');
    expect(auditOperationKeyOf('end')).toBe('end');
    expect(auditOperationKeyOf('view')).toBe('view');
    expect(auditOperationKeyOf('delete')).toBe('unknown');
  });
});

describe('sameUser（大小を無視・空は一致しない）', () => {
  it('比べる', () => {
    expect(sameUser('Admin', 'admin')).toBe(true);
    expect(sameUser(' admin ', 'admin')).toBe(true);
    expect(sameUser('admin', 'other')).toBe(false);
    expect(sameUser('', '')).toBe(false);
    expect(sameUser(undefined, 'admin')).toBe(false);
  });
});

describe('canOfferView（SC-03 への導線の出し分け。許可ではない）', () => {
  const none = new Set<string>();

  it('自分が指名された・発動の行・終了予定が未来なら出す', () => {
    expect(canOfferView(entry(), 'admin', NOW, none)).toBe(true);
    expect(canOfferView(entry(), 'ADMIN', NOW, none)).toBe(true);
  });

  it.each([
    ['他の人が指名された', entry({ nomineeUserId: 'other' }), none],
    ['発動以外の行（閲覧）', entry({ operation: 'view' }), none],
    ['発動以外の行（終了）', entry({ operation: 'end' }), none],
    ['失効した', entry({ endsAt: '2026-10-24T02:59:59Z' }), none],
    ['ちょうど終了予定', entry({ endsAt: '2026-10-24T03:00:00Z' }), none],
    ['終了の行がある', entry(), new Set(['g'])],
  ])('%s → 出さない', (_label, e, ended) => {
    expect(canOfferView(e, 'admin', NOW, ended)).toBe(false);
  });

  it('身元が無ければ出さない', () => {
    expect(canOfferView(entry(), undefined, NOW, none)).toBe(false);
  });
});

describe('endedGrantIdsOf / auditUsernamesOf', () => {
  it('終了の行の発動だけを集める', () => {
    const set = endedGrantIdsOf([
      entry({ grantId: 'g1', operation: 'end' }),
      entry({ grantId: 'g2' }),
      entry({ grantId: 'g3', operation: 'view' }),
    ]);
    expect([...set]).toEqual(['g1']);
  });

  it('実行者・所有者・指名先を重複なく集め、100 件で切る（契約の上限）', () => {
    expect(auditUsernamesOf([entry(), entry({ actorUserId: 'admin' })]).sort()).toEqual([
      'admin',
      'owner',
    ]);
    const many = Array.from({ length: 60 }, (_, i) =>
      entry({ actorUserId: `a${i}`, ownerUserId: `o${i}`, nomineeUserId: 'n' }),
    );
    expect(auditUsernamesOf(many)).toHaveLength(100);
  });
});
