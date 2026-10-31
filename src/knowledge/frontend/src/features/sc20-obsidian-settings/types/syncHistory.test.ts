import { describe, it, expect } from 'vitest';
import { i18n } from '@lingui/core';
import type { MessageDescriptor } from '@lingui/core';
import type { SyncHistoryEntryDto } from '@foundation/api/generated/bff.schemas';
import {
  breakdownOf,
  directionLabel,
  failureReasonText,
  isPausedPeriod,
  isSuccess,
} from './syncHistory';

// SC-20 主要素 6, FR-20, ADR-0099 決定 5・6 / IADR-0446（#1446）: 同期履歴の写像。
//
// 🔴 **失敗理由の 7 値はここへ独立に列挙する。** 実装が持つ配列を import すると、
// **片方を消しても両方が同時に変わり**「契約の 7 値すべてに文言が在る」ことを確かめたことにならない。
// 出典は `docs/api/openapi.yaml` の `SyncHistoryEntryDto.failureReason`（契約が正）。
const CONTRACT_REASONS = [
  'version_conflict',
  'deleted',
  'path_conflict',
  'quota_exceeded',
  'body_too_large',
  'invalid_request',
  'not_found',
  // #2379, ADR-0145 決定 3: 一時停止中の拒否（停止期間ごとの 1 行）。
  'sync_paused',
] as const;

function textOf(label: MessageDescriptor | string): string {
  return typeof label === 'string' ? label : i18n._(label);
}

function entry(counts: Partial<SyncHistoryEntryDto> = {}): SyncHistoryEntryDto {
  return {
    id: 'h1',
    occurredAt: '2026-09-12T00:00:00Z',
    deviceName: 'MacBook Pro',
    direction: 'push',
    added: 0,
    updated: 0,
    deleted: 0,
    conflicted: 0,
    outcome: 'success',
    failureReason: null,
    ...counts,
  };
}

describe('SC-20 同期履歴: 方向', () => {
  it('push は「送信」・pull は「受信」（端末から見た向き）', () => {
    expect(textOf(directionLabel('push'))).toBe('送信');
    expect(textOf(directionLabel('pull'))).toBe('受信');
  });

  it('未知の方向は生値をそのまま返す（既知へ丸めない）', () => {
    expect(textOf(directionLabel('sideways'))).toBe('sideways');
    // 陰性の対: 既知の文言へ化けていない。
    expect(textOf(directionLabel('sideways'))).not.toBe('送信');
    expect(textOf(directionLabel('sideways'))).not.toBe('受信');
  });
});

describe('SC-20 同期履歴: 結果', () => {
  it('success だけを成功と読み、それ以外は成功にしない', () => {
    expect(isSuccess('success')).toBe(true);
    expect(isSuccess('failure')).toBe(false);
    // 未知の値を成功へ倒さない（倒すと失敗が緑のバッジで出る）。
    expect(isSuccess('SUCCESS')).toBe(false);
    expect(isSuccess('')).toBe(false);
  });
});

describe('SC-20 同期履歴: 失敗理由の文言（ADR-0099 決定 5）', () => {
  it('契約の 7 値すべてに、次の行動が分かる文言が在る', () => {
    for (const code of CONTRACT_REASONS) {
      const text = textOf(failureReasonText(code));
      // ★ 陽性対照: 文言が在り、**コードそのままではない**。
      expect(text, code).not.toBe(code);
      expect(text.length, code).toBeGreaterThan(5);
      // 🔴 利用者向けの文言であることの手掛かり: 句点で終わる日本語の文である。
      expect(text, code).toMatch(/。$/);
    }
  });

  it('個々の文言が、その理由に対する具体的な次の行動を指す', () => {
    expect(textOf(failureReasonText('version_conflict'))).toContain('同期の競合');
    expect(textOf(failureReasonText('deleted'))).toContain('削除済みタブ');
    expect(textOf(failureReasonText('path_conflict'))).toContain('ファイル名');
    expect(textOf(failureReasonText('quota_exceeded'))).toContain('容量');
    expect(textOf(failureReasonText('body_too_large'))).toContain('1 MB');
    expect(textOf(failureReasonText('invalid_request'))).toContain('プラグイン');
    expect(textOf(failureReasonText('not_found'))).toContain('再同期');
  });

  it('7 値の文言はすべて異なる（同じ文面へ丸めていない）', () => {
    const texts = CONTRACT_REASONS.map((code) => textOf(failureReasonText(code)));
    expect(new Set(texts).size).toBe(CONTRACT_REASONS.length);
  });

  it('未知のコードは、コードを添えた文言にする（黙って落とさない）', () => {
    const text = textOf(failureReasonText('meteor_strike'));
    expect(text).toContain('meteor_strike');
    expect(text).toContain('同期に失敗しました');
    // 陰性の対: 既知の文言へ化けていない。
    expect(text).not.toContain('同期の競合');
  });
});

describe('SC-20 同期履歴: 件数の内訳', () => {
  it('0 の項目を落とし、0 でない項目だけを順序どおり返す', () => {
    expect(breakdownOf(entry({ added: 1 }))).toEqual([{ kind: 'added', count: 1 }]);
    expect(breakdownOf(entry({ updated: 2, conflicted: 1 }))).toEqual([
      { kind: 'updated', count: 2 },
      { kind: 'conflicted', count: 1 },
    ]);
    expect(breakdownOf(entry({ added: 1, updated: 1, deleted: 1, conflicted: 1 }))).toEqual([
      { kind: 'added', count: 1 },
      { kind: 'updated', count: 1 },
      { kind: 'deleted', count: 1 },
      { kind: 'conflicted', count: 1 },
    ]);
  });

  it('すべて 0 なら空配列（呼び出し側が「—」を描く）', () => {
    expect(breakdownOf(entry())).toEqual([]);
  });
});

// FR-20, SC-20 主要素 6・7, 計画 ADR-0145 決定 3, [[IADR-0668]] (#2379): 停止期間ごとの拒否の行。
describe('SC-20 同期履歴: 停止期間の行（#2379）', () => {
  const paused = entry({
    deviceName: '',
    direction: '',
    outcome: 'failure',
    failureReason: 'sync_paused',
    rejectedCount: 3,
    firstRejectedAt: '2026-10-31T01:00:00Z',
    lastRejectedAt: '2026-10-31T05:00:00Z',
  });

  it('停止期間の行だけを停止期間の行と読む（陰性: 通常の失敗の行・回数の無い行）', () => {
    expect(isPausedPeriod(paused)).toBe(true);
    expect(isPausedPeriod(entry({ outcome: 'failure', failureReason: 'version_conflict' }))).toBe(
      false,
    );
    expect(isPausedPeriod(entry())).toBe(false);
  });

  it('内訳は「拒否 N 回」だけを返す（0 の項目は出さない）', () => {
    expect(breakdownOf(paused)).toEqual([{ kind: 'rejected', count: 3 }]);
  });

  it('失敗理由は、停止中だったことと、再開すれば同じトークンで戻ることを伝える', () => {
    const text = textOf(failureReasonText('sync_paused'));
    expect(text).toMatch(/一時停止/);
    expect(text).toMatch(/同じトークン/);
    expect(text).not.toMatch(/理由: sync_paused/);
  });
});
