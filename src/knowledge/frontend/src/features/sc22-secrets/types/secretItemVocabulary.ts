import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-22, ADR-0095 決定 1, IADR-0453 決定 2・6: 画面が持つ語彙と、更新フォームの入力規則（純関数）。
//
// ■ 表示名と用途は**画面の側**（翻訳カタログ）に持つ
//   allowlist（deploy/bootstrap/sc22-secret-items.json）の `why` は「なぜ画面の対象に入れたか」の記録であり、
//   利用者向けの用途の説明ではない。また翻訳されない。**未知の項目は識別子をそのまま出す**（隠さない）。

export interface SecretItemLabel {
  name: MessageDescriptor;
  purpose: MessageDescriptor;
}

const LABELS: Readonly<Record<string, SecretItemLabel>> = {
  'llm-provider-credentials': {
    name: msg`外部 LLM の API キー`,
    purpose: msg`検索・質問応答・分析で外部の LLM を呼び出すための API キーです。`,
  },
  'keycloak-smtp': {
    name: msg`メール送信（SMTP）の認証情報`,
    purpose: msg`認証基盤がパスワード再設定などの通知メールを送るための送信元アドレスと資格情報です（宛先ホスト・ポートは構成であり、ここでは扱いません）。`,
  },
  'wikijs-sync': {
    name: msg`Wiki 同期の API キー`,
    purpose: msg`文書を Wiki.js と同期するための接続キーです。`,
  },
  'ast-app-secrets': {
    name: msg`株式自動売買の外部 API キーと通知`,
    purpose: msg`市場データ・経済指標・開示情報の API キーと、Discord 通知の webhook・トークンです。`,
  },
};

/** 項目の表示名と用途。未登録の項目は null（呼び出し側は識別子をそのまま出す）。 */
export function secretItemLabel(item: string): SecretItemLabel | null {
  return Object.prototype.hasOwnProperty.call(LABELS, item) ? LABELS[item] : null;
}

/** 値の上限（BFF の `SecretItemBffEndpoints.MaxValueLength` と一致させる）。 */
export const MAX_VALUE_LENGTH = 8192;
/** 更新の理由の上限（BFF の `SecretItemBffEndpoints.MaxReasonLength` と一致させる）。 */
export const MAX_REASON_LENGTH = 500;

export type SecretUpdateIssue =
  | 'property-required'
  | 'value-required'
  | 'value-too-long'
  | 'confirmation-mismatch'
  | 'reason-too-long';

export interface SecretUpdateDraft {
  property: string;
  value: string;
  confirmation: string;
  reason: string;
}

/**
 * 更新フォームの入力規則（SC-22 入力/バリデーション）。1 件でも返れば送信しない。
 *
 * 🔴 **確認入力は完全一致**（前後の空白も含めて比べる）。貼り付けの誤りを保存前に捕まえるためであり、
 * trim すると「空白が混ざった値」を捕まえられない。
 */
export function secretUpdateIssues(
  draft: SecretUpdateDraft,
  writableProperties: readonly string[],
): SecretUpdateIssue[] {
  const issues: SecretUpdateIssue[] = [];
  if (!writableProperties.includes(draft.property)) issues.push('property-required');
  if (draft.value.length === 0) issues.push('value-required');
  if (draft.value.length > MAX_VALUE_LENGTH) issues.push('value-too-long');
  if (draft.value !== draft.confirmation) issues.push('confirmation-mismatch');
  if (draft.reason.trim().length > MAX_REASON_LENGTH) issues.push('reason-too-long');
  return issues;
}
