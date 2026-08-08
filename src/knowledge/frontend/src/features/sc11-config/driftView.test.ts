import { describe, it, expect } from 'vitest';
import { i18n } from '@foundation/i18n';
import {
  driftKindLabel,
  driftSeverityView,
  driftTargets,
  hadDriftLabel,
  DRIFT_KINDS,
  DRIFT_SEVERITIES,
} from './driftView';

// SC-11, FR-15: ドリフトの表示写像（純関数）。
//
// **値集合を純関数テストで固定する**理由（IADR-0129 決定 6）: #503 の変異試験 M31 は
// 「値集合から 1 値落としても画面テストは 1 件も落ちない」ことを実測した。画面テストは
// 「その値がモックデータに含まれていたか」しか見ないため、間接被覆では欠落を検出できない。

function text(label: ReturnType<typeof driftKindLabel>): string {
  return typeof label === 'string' ? label : i18n._(label);
}

describe('driftView (SC-11)', () => {
  // 契約（DriftDetector）が返す種別は 5 値である。
  it('fixes exactly the five drift kinds the contract emits', () => {
    expect([...DRIFT_KINDS]).toEqual([
      'MissingApply',
      'UndeclaredSubscription',
      'StaleStage',
      'BindingMismatch',
      'Unverifiable',
    ]);
  });

  // 契約（DriftDetector.SeverityWarning / SeverityInfo）が返す深刻度は 2 値である。
  it('fixes exactly the two severities the contract emits', () => {
    expect([...DRIFT_SEVERITIES]).toEqual(['Warning', 'Info']);
  });

  it.each([
    ['MissingApply', '適用漏れ（宣言にあり実効に無い）'],
    ['UndeclaredSubscription', '宣言に無い購読'],
    ['StaleStage', '古い段の残留'],
    ['BindingMismatch', '接続の不一致'],
    ['Unverifiable', '検証不能（担当サービスへ到達できない）'],
  ])('maps the drift kind %s to a readable label', (kind, label) => {
    expect(text(driftKindLabel(kind))).toBe(label);
  });

  // 未知の種別を握り潰さない（`—` や「不明」へ丸めない）。契約が 6 つ目を返したら画面で気付ける。
  it('shows an unknown drift kind verbatim instead of hiding it', () => {
    expect(driftKindLabel('QueueNameMismatch')).toBe('QueueNameMismatch');
  });

  // INDEX 決定 21: 色だけで意味を持たせない。tone とテキストが必ず対で決まる。
  it.each([
    ['Warning', '警告', 'warning'],
    ['Info', '情報', 'neutral'],
  ])('maps the severity %s to a labelled badge', (severity, label, tone) => {
    const view = driftSeverityView(severity);
    expect(text(view.label)).toBe(label);
    expect(view.tone).toBe(tone);
  });

  it('shows an unknown severity verbatim and falls back to a neutral tone', () => {
    const view = driftSeverityView('Critical');
    expect(view.label).toBe('Critical');
    expect(view.tone).toBe('neutral');
  });

  // DriftDetector は Target に常に段名だけを返す。強調判定はその集合で行う。
  it('collects the drift targets for highlighting the pipeline chain', () => {
    const targets = driftTargets([{ target: 'embed' }, { target: 'index' }, { target: 'embed' }]);
    expect([...targets].sort()).toEqual(['embed', 'index']);
  });

  // IADR-0046: hadDrift は 3 値である（真 / 偽 / 不明）。null を false へ丸めると
  // 「ドリフトが無かった」と誤って読める。
  it('keeps the three-valued hadDrift distinct (true / false / unknown)', () => {
    expect(text(hadDriftLabel(true))).toBe('あり');
    expect(text(hadDriftLabel(false))).toBe('なし');
    expect(text(hadDriftLabel(null))).toBe('—');
    expect(text(hadDriftLabel(undefined))).toBe('—');
  });
});
