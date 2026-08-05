import { describe, it, expect } from 'vitest';
import { i18n } from '@foundation/i18n';
import { formatDateTime, SOURCE_TYPES, sourceTypeLabel, syncStateView } from './syncState';

// SC-06, UC-04, FR-01/FR-02: 同期状態と種別の写像（純関数）。
// IADR-0127 決定 2: 契約から導出できる値だけで状態を表す。

const label = (value: ReturnType<typeof sourceTypeLabel>) =>
  typeof value === 'string' ? value : i18n._(value);

describe('syncState (SC-06)', () => {
  // 05_screens モック間相違の確定 ②「SC-06 の**同期異常表示**の警告色＝琥珀」。琥珀が指すのは
  // 異常であり、管理者が意図して無効化した正常な設定状態ではない。同期健全性が契約に載るまで
  // 琥珀は空けておく（IADR-0127 決定 2）。INDEX 決定 21 により色 ＋ アイコン ＋ テキストは維持する。
  it('marks a disabled source as neutral, leaving amber for a real sync fault', () => {
    const view = syncStateView('disabled', '2026-08-01T03:00:00Z');
    expect(view.tone).toBe('neutral');
    expect(i18n._(view.label)).toBe('無効');
    // 無効なソースに「同期済み（日時）」を出すと、取り込みが続いているように読める。
    expect(view.showSyncedAt).toBe(false);
  });

  // 契約から導出できる 3 状態のどれにも琥珀を充てない（充ててよいのは同期異常だけである）。
  it('never uses the amber warning tone for any state the contract can express', () => {
    const views = [
      syncStateView('disabled', null),
      syncStateView('active', '2026-08-01T03:00:00Z'),
      syncStateView('active', null),
    ];
    expect(views.map((v) => v.tone)).not.toContain('warning');
  });

  it('marks an active source with a sync timestamp as synchronised', () => {
    const view = syncStateView('active', '2026-08-01T03:00:00Z');
    expect(view.tone).toBe('success');
    expect(i18n._(view.label)).toBe('同期済み');
    expect(view.showSyncedAt).toBe(true);
  });

  it('marks an active source without a sync timestamp as not synchronised', () => {
    const view = syncStateView('active', null);
    expect(view.tone).toBe('neutral');
    expect(i18n._(view.label)).toBe('未同期');
    expect(view.showSyncedAt).toBe(false);
  });

  // 05_screens §SC-06「種別はファイルサーバー・Wiki・SaaS・業務DB」。
  it('names the four source types the plan lists', () => {
    expect([...SOURCE_TYPES]).toEqual(['filesystem', 'wiki', 'saas', 'db']);
    expect(label(sourceTypeLabel('filesystem'))).toBe('ファイルサーバー');
    expect(label(sourceTypeLabel('wiki'))).toBe('Wiki');
    expect(label(sourceTypeLabel('saas'))).toBe('SaaS');
    expect(label(sourceTypeLabel('db'))).toBe('業務DB');
  });

  // 未知の種別を空欄へ丸めない（サーバが 5 つ目を返したら画面で気付ける）。
  it('shows an unknown source type verbatim', () => {
    expect(sourceTypeLabel('mailbox')).toBe('mailbox');
  });

  it('formats timestamps and keeps unparsable values visible', () => {
    expect(formatDateTime(null)).toBe('—');
    expect(formatDateTime('')).toBe('—');
    expect(formatDateTime('not-a-date')).toBe('not-a-date');
    expect(formatDateTime('2026-08-01T03:00:00Z')).not.toBe('—');
  });
});
