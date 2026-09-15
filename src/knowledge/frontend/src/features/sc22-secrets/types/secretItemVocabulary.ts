import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-22, ADR-0095 決定 1, IADR-0453 決定 2・6, IADR-0456 決定 1 (#1477): 画面が持つ語彙と、更新フォームの入力規則（純関数）。
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
    purpose: msg`市場データ・経済指標・開示情報の API キーと、Discord 通知の webhook・トークン・環境ごとの ID（サーバー・チャンネル・許可する利用者）です。`,
  },
  'ast-moomoo': {
    name: msg`moomoo 証券のログイン情報`,
    purpose: msg`OpenD が moomoo 証券へログインするための ID とパスワードです。パスワードは MD5 に変換した値だけが保存されます。`,
  },
  'ast-moomoo-rsa': {
    name: msg`OpenD の RSA 鍵`,
    purpose: msg`OpenD との通信に使う RSA 秘密鍵です。値は入力せず、この画面の「生成」で作ります。`,
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

/** プロパティの値の作り方（BFF の `SecretPropertyKind`・契約の `SecretItemPropertyDto.kind`）。 */
type SecretPropertyKind = 'value' | 'md5-from-password' | 'generate-rsa-pkcs1';

export interface SecretPropertyShape {
  name: string;
  kind: SecretPropertyKind;
  sensitive: boolean;
}

const KINDS: readonly SecretPropertyKind[] = ['value', 'md5-from-password', 'generate-rsa-pkcs1'];

interface SecretPropertyDetailLike {
  name: string;
  kind: string;
  sensitive: boolean;
}

/**
 * 一覧の 1 行から、書けるプロパティごとの入力の形を作る（IADR-0456 決定 1）。
 *
 * 🔴 **分からないときは「値・秘密」に倒す**（マスクして確認入力を求める側）。種別の宣言が無い・未知の種別のとき、
 * 平文の入力や生成のボタンを出さない —— 秘密を平文で見せる誤りのほうが重い。
 * 秘密でない（`sensitive: false`）と扱うのは、BFF が種別 `value` についてそう宣言したときだけである。
 */
export function secretPropertyShapes(row: {
  properties: readonly string[];
  propertyDetails?: readonly SecretPropertyDetailLike[] | null;
}): SecretPropertyShape[] {
  return row.properties.map((name) => {
    const detail = row.propertyDetails?.find((d) => d.name === name);
    const kind = KINDS.find((k) => k === detail?.kind) ?? 'value';
    const sensitive = !(detail && kind === 'value' && detail.sensitive === false);
    return { name, kind, sensitive };
  });
}

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
 * IADR-0456: 生成（`generate-rsa-pkcs1`）は値を入力しない（値の規則を当てない）。秘密でない値は平文で見えるので確認入力を求めない。
 */
export function secretUpdateIssues(
  draft: SecretUpdateDraft,
  properties: readonly SecretPropertyShape[],
): SecretUpdateIssue[] {
  const issues: SecretUpdateIssue[] = [];
  const shape = properties.find((p) => p.name === draft.property);
  if (!shape) issues.push('property-required');
  const kind = shape?.kind ?? 'value';
  if (kind !== 'generate-rsa-pkcs1') {
    if (draft.value.length === 0) issues.push('value-required');
    if (draft.value.length > MAX_VALUE_LENGTH) issues.push('value-too-long');
    const needsConfirmation = shape?.sensitive ?? true;
    if (needsConfirmation && draft.value !== draft.confirmation)
      issues.push('confirmation-mismatch');
  }
  if (draft.reason.trim().length > MAX_REASON_LENGTH) issues.push('reason-too-long');
  return issues;
}
