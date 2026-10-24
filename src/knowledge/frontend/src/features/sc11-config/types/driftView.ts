import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-11, FR-15: ドリフト（宣言 Git と実効構成の差分）の表示写像（純関数）。
//
// 契約（Platform.Shared.Infrastructure/Foundation/Introspection/DriftDetector.cs）が返す
// 種別 5 値・深刻度 2 値をそのまま写す。
//
// 🔴 **［2026-10-24 / #2022・[[IADR-0620]]］種別と深刻度は契約の値のまま出す（日本語に訳さない）。**
// 計画 05_screens §SC-11 は「深刻度と種別は契約が返す値をそのまま表示する（日本語に訳さない）。
// 運用者・管理者向けであり、実装との突合を容易にするため」と定める（2026-08-05 確定・Q29）。
// 従前は IADR-0129 が表示名（「警告」「接続の不一致」等）を入れていたが、計画の確定を IADR だけでは
// 覆せないので戻した。**色だけで意味を伝えない状態は保つ**（tone ごとの固定アイコン ＋ 契約の値の文字）。**判定を DOM から切り離してあるのは、値集合そのものを
// 描画なしで試験できるようにするためである** —— #503 の変異試験 M31 は「値集合から 1 値落としても
// 画面テストは 1 件も落ちない」ことを実測した（IADR-0129 決定 6）。

/** 契約が返すドリフト種別の 5 値（`DriftDetector` の定数）。 */
export const DRIFT_KINDS = [
  'MissingApply',
  'UndeclaredSubscription',
  'StaleStage',
  'BindingMismatch',
  'Unverifiable',
] as const;

/** 契約が返す深刻度の 2 値（`DriftDetector.SeverityWarning` / `SeverityInfo`）。 */
export const DRIFT_SEVERITIES = ['Warning', 'Info'] as const;

export type DriftSeverity = (typeof DRIFT_SEVERITIES)[number];

/** `StatusBadge` の tone。tone ごとに固定アイコンが付く（INDEX 決定 21 を型で強制する部品）。 */
export type DriftTone = 'neutral' | 'warning';

export interface DriftSeverityView {
  /** 表示文言。**契約の値そのもの**（`Warning` / `Info`。未知の値も生値。翻訳しない。IADR-0620）。 */
  label: string;
  tone: DriftTone;
}

const SEVERITY_TONES: Record<DriftSeverity, DriftTone> = {
  Warning: 'warning',
  Info: 'neutral',
};

function isKnownSeverity(severity: string): severity is DriftSeverity {
  return (DRIFT_SEVERITIES as readonly string[]).includes(severity);
}

/**
 * 深刻度を表示の組（文言 ＋ tone）へ写像する。文言は契約の値そのもので、写すのは tone だけである。
 *
 * **未知の値は握り潰さない**——`—` や「不明」へ丸めず生値をそのまま出す。契約が 2 値と定めていても、
 * サーバが将来 3 つ目を返したときに画面が異常へ気付ける状態にしておく。
 */
export function driftSeverityView(severity: string): DriftSeverityView {
  return {
    label: severity,
    tone: isKnownSeverity(severity) ? SEVERITY_TONES[severity] : 'neutral',
  };
}

/**
 * ドリフト種別の表示文言。**契約の値そのもの**を返す（訳さない。計画 §SC-11・IADR-0620）。
 * 未知の値も生値のまま（丸めない）。
 */
export function driftKindLabel(kind: string): string {
  return kind;
}

/**
 * ドリフト対象（`finding.target`）の集合。実効構成側（段）の強調判定に用いる。
 *
 * `DriftDetector` は `Target` に常に段名（宣言側の `Name` または実効段名）だけを返し、
 * イベント名・ポート名は返さない。したがって強調表示の対象はパイプライン段に限られる
 * （`CLAUDE.md`「起こり得ないケースへの防御的実装を避ける」）。
 */
export function driftTargets(findings: readonly { target: string }[]): Set<string> {
  return new Set(findings.map((f) => f.target));
}

/**
 * 履歴エントリの「その時点のドリフト有無」を表示文言へ写像する。
 *
 * IADR-0046: 注入時に判明していれば `true` / `false`、**不明なら `null`**（＝「—」）。
 * 3 値であり真偽 2 値ではない——`null` を `false` へ丸めると「ドリフトが無かった」と誤って読める。
 */
export function hadDriftLabel(hadDrift: boolean | null | undefined): MessageDescriptor {
  if (hadDrift === true) return msg`あり`;
  if (hadDrift === false) return msg`なし`;
  return msg`—`;
}
