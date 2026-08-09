import { describe, it, expect } from 'vitest';
import { i18n } from '@foundation/i18n';
import { formatDateTime, SOURCE_TYPES, sourceTypeLabel, syncStateView } from './syncState';

// SC-06, UC-04, FR-01/FR-02: 同期状態と種別の写像（純関数）。
// IADR-0127 決定 2: 契約から導出できる値だけで状態を表す。

const label = (value: ReturnType<typeof sourceTypeLabel>) =>
  typeof value === 'string' ? value : i18n._(value);

describe('syncState (SC-06)', () => {
  // 05_screens モック間相違の確定 ②「SC-06 の**同期異常表示**の警告色＝琥珀」。琥珀が指すのは
  // 異常であり、管理者が意図して無効化した正常な設定状態ではない（IADR-0127 決定 2。計画も
  // 「妥当でありそのまま活かす」と明記）。INDEX 決定 21 により色 ＋ アイコン ＋ テキストは維持する。
  it('marks a disabled source as neutral, leaving amber for a real sync fault', () => {
    const view = syncStateView('disabled', '2026-08-01T03:00:00Z');
    expect(view.tone).toBe('neutral');
    expect(i18n._(view.label)).toBe('無効');
    // 無効なソースに「同期済み（日時）」を出すと、取り込みが続いているように読める。
    expect(view.showSyncedAt).toBe(false);
  });

  // **無効化されたソースは同期が回らない**ので、残っている失敗回数を異常として出し続けない
  // （管理者が意図して止めた正常な状態に ⚠ が付く）。健全性より状態を先に見る理由である。
  it('keeps a disabled source neutral even when failures are still recorded', () => {
    const view = syncStateView('disabled', null, { consecutiveFailureCount: 5, retryLimit: 5 });
    expect(view.tone).toBe('neutral');
    expect(i18n._(view.label)).toBe('無効');
  });

  // 健全性を伴わない（失敗 0 の）状態にはどれにも琥珀を充てない。
  it('never uses the amber warning tone for a healthy source', () => {
    const views = [
      syncStateView('disabled', null),
      syncStateView('active', '2026-08-01T03:00:00Z'),
      syncStateView('active', null),
      syncStateView('active', '2026-08-01T03:00:00Z', {
        consecutiveFailureCount: 0,
        retryLimit: 5,
      }),
    ];
    expect(views.map((v) => v.tone)).not.toContain('warning');
  });

  // SC-06（裁定 Q14 / #537）: hi-fi の「⚠ 再試行中（3/5）」。上限に達する前から琥珀で見せる
  // ——「静かに壊れる」機能に気づく手段が本表示だからである。
  it('shows an amber retrying state below the retry limit', () => {
    const view = syncStateView('active', '2026-08-01T03:00:00Z', {
      consecutiveFailureCount: 3,
      retryLimit: 5,
    });
    expect(view.tone).toBe('warning');
    expect(i18n._(view.label)).toBe('再試行中（3/5）');
    // 異常時に「同期済み（日時）」を併記すると、取り込みが続いているように読める。
    expect(view.showSyncedAt).toBe(false);
  });

  // 計画 §SC-06:「「継続失敗」のしきい値は**再試行上限に達した時点**とする」。
  it('escalates to a sync fault exactly at the retry limit', () => {
    const view = syncStateView('active', null, { consecutiveFailureCount: 5, retryLimit: 5 });
    expect(view.tone).toBe('warning');
    expect(i18n._(view.label)).toBe('同期異常（5/5）');
  });

  // しきい値は契約が返す retryLimit である（画面に定数を複写しない）。
  // サーバが上限を 3 に変えたら、3 回で「同期異常」へ上がる。
  it('takes the threshold from the contract, not from a copied constant', () => {
    const view = syncStateView('active', null, { consecutiveFailureCount: 3, retryLimit: 3 });
    expect(i18n._(view.label)).toBe('同期異常（3/3）');
  });

  // 契約が健全性を返さない相手（古い後段・部分的な応答）でも壊れない。
  it('falls back to the plain states when health is absent', () => {
    expect(syncStateView('active', null, {}).tone).toBe('neutral');
    expect(
      syncStateView('active', null, { consecutiveFailureCount: null, retryLimit: null }).tone,
    ).toBe('neutral');
    // 上限が不明なまま失敗回数だけ来ても「n/0」のような無意味な表示を作らない。
    expect(syncStateView('active', null, { consecutiveFailureCount: 2 }).tone).toBe('neutral');
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
