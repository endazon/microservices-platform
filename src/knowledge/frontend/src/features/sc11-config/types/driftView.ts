import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-11, FR-15: ドリフト（宣言 Git と実効構成の差分）の表示写像（純関数）。
//
// 契約（Platform.Shared.Infrastructure/Foundation/Introspection/DriftDetector.cs）が返す
// 種別 5 値・深刻度 2 値をそのまま写す。**判定を DOM から切り離してあるのは、値集合そのものを
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

export type DriftKind = (typeof DRIFT_KINDS)[number];

/** 契約が返す深刻度の 2 値（`DriftDetector.SeverityWarning` / `SeverityInfo`）。 */
export const DRIFT_SEVERITIES = ['Warning', 'Info'] as const;

export type DriftSeverity = (typeof DRIFT_SEVERITIES)[number];

/** `StatusBadge` の tone。tone ごとに固定アイコンが付く（INDEX 決定 21 を型で強制する部品）。 */
export type DriftTone = 'neutral' | 'warning';

export interface DriftSeverityView {
  /** 表示文言。**未知の深刻度は生値を出すため `string` になる**（翻訳しない）。 */
  label: MessageDescriptor | string;
  tone: DriftTone;
}

const SEVERITY_VIEWS: Record<DriftSeverity, DriftSeverityView> = {
  Warning: { label: msg`警告`, tone: 'warning' },
  Info: { label: msg`情報`, tone: 'neutral' },
};

const KIND_LABELS: Record<DriftKind, MessageDescriptor> = {
  MissingApply: msg`適用漏れ（宣言にあり実効に無い）`,
  UndeclaredSubscription: msg`宣言に無い購読`,
  StaleStage: msg`古い段の残留`,
  BindingMismatch: msg`接続の不一致`,
  Unverifiable: msg`検証不能（担当サービスへ到達できない）`,
};

function isKnownSeverity(severity: string): severity is DriftSeverity {
  return (DRIFT_SEVERITIES as readonly string[]).includes(severity);
}

function isKnownKind(kind: string): kind is DriftKind {
  return (DRIFT_KINDS as readonly string[]).includes(kind);
}

/**
 * 深刻度を表示の組（文言 ＋ tone）へ写像する。
 *
 * **未知の値は握り潰さない**——`—` や「不明」へ丸めず生値をそのまま出す。契約が 2 値と定めていても、
 * サーバが将来 3 つ目を返したときに画面が異常へ気付ける状態にしておく。
 */
export function driftSeverityView(severity: string): DriftSeverityView {
  return isKnownSeverity(severity)
    ? SEVERITY_VIEWS[severity]
    : { label: severity, tone: 'neutral' };
}

/** ドリフト種別を表示名へ写像する。未知の値は生値をそのまま返す（丸めない）。 */
export function driftKindLabel(kind: string): MessageDescriptor | string {
  return isKnownKind(kind) ? KIND_LABELS[kind] : kind;
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
