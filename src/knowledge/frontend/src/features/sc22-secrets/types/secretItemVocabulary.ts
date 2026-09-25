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

/**
 * 項目の「いま効いている供給元」（SC-22 主要素 1, ADR-0104 決定 2, IADR-0460 決定 1。契約の `supplySource`）。
 *
 * - `screen`: 同期先の ExternalSecret が在る —— この画面で書いた値が届く。
 * - `git`: 同期先が無い —— 値は配備時の設定（Git 経路）から来る。書いた値は届かない。
 * - `unknown`: BFF が判定できなかった。
 *
 * 🔴 **画面は推測しない。** 値が無い・未知の値は `unknown` として扱う（`screen` にも `git` にも倒さない）。
 */
export type SecretSupplySource = 'screen' | 'git' | 'unknown';

export function secretSupplySource(row: { supplySource?: string | null }): SecretSupplySource {
  return row.supplySource === 'screen' || row.supplySource === 'git' ? row.supplySource : 'unknown';
}

/**
 * 書き込んだ値を読む消費側の作り直され方（ADR-0104 決定 4, IADR-0460 決定 2）。
 *
 * - `automatic`: 消費側は env で読み、Secret の変化で Reloader が作り直す（IADR-0456 決定 5 の注釈を持つ消費側）。
 *   🔴 Reloader を配備した環境（連結ローカルの ESO=1）に限る —— 画面の文言もそう書く。
 * - `manual-opend`: 消費側は OpenD であり Reloader の対象外（AST の IADR-0341 決定 4）。手動の再起動が要る。
 *
 * 表に無い項目は `null`（呼び出し側は「再起動されることがある」とだけ書く。**断定しない**）。
 */
export type SecretConsumerRestart = 'automatic' | 'manual-opend';

const RESTARTS: Readonly<Record<string, SecretConsumerRestart>> = {
  'llm-provider-credentials': 'automatic',
  'keycloak-smtp': 'automatic',
  'wikijs-sync': 'automatic',
  'ast-app-secrets': 'automatic',
  'ast-moomoo': 'manual-opend',
  'ast-moomoo-rsa': 'manual-opend',
};

export function secretConsumerRestart(item: string): SecretConsumerRestart | null {
  return Object.prototype.hasOwnProperty.call(RESTARTS, item) ? RESTARTS[item] : null;
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
