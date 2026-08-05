import { describe, it, expect } from 'vitest';
import { i18n } from '@foundation/i18n';
import { formatDateTime, SOURCE_TYPES, sourceTypeLabel, syncStateView } from './syncState';

// SC-06, UC-04, FR-01/FR-02: 同期状態と種別の写像（純関数）。
// IADR-0127 決定 2: 契約から導出できる値だけで状態を表す。

const label = (value: ReturnType<typeof sourceTypeLabel>) =>
  typeof value === 'string' ? value : i18n._(value);

describe('syncState (SC-06)', () => {
  // 05_screens モック間相違の確定 ②「SC-06 の同期異常表示の警告色＝琥珀」。
  // 同期異常そのものは契約に無いため、琥珀が指すべき意味（取り込みが行われていない）を
  // `disabled` へ充てる。INDEX 決定 21 により色は tone が担い、アイコンとテキストが必ず伴う。
  it('marks a disabled source with the amber warning tone', () => {
    const view = syncStateView('disabled', '2026-08-01T03:00:00Z');
    expect(view.tone).toBe('warning');
    expect(i18n._(view.label)).toBe('無効');
    // 無効なソースに「同期済み（日時）」を出すと、取り込みが続いているように読める。
    expect(view.showSyncedAt).toBe(false);
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
