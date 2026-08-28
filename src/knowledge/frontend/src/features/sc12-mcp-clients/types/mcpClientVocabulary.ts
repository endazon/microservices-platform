import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';
import type { AttributeDefinitionDto } from '@foundation/api/generated/bff.schemas';

// SC-12, UC-09, FR-16: 本画面の語彙と入力規則（純関数）。
//
// **判定を DOM から切り離してある。** 値集合と必須判定そのものを描画なしで試験するためである
// （#503 の変異試験が「値集合から 1 値落としても画面テストは 1 件も落ちない」ことを実測した。
// IADR-0129 決定 6）。

/**
 * クライアント種別（契約 `McpClientView.kind` / `RegisterMcpClientRequest.kind` の 2 値）。
 *
 * 05_screens §SC-12 入力/バリデーション: 有人（Authorization Code + PKCE）／
 * 無人（Client Credentials）。**3 値目を作らない。**
 */
export const CLIENT_KINDS = ['interactive', 'service-account'] as const;

export type ClientKind = (typeof CLIENT_KINDS)[number];

const KIND_LABELS: Record<ClientKind, MessageDescriptor> = {
  interactive: msg`有人`,
  'service-account': msg`無人`,
};

/** 種別ごとの認証方式（計画の表記そのまま。モックの「認証」列に対応する）。 */
const KIND_AUTH_LABELS: Record<ClientKind, MessageDescriptor> = {
  interactive: msg`Authorization Code + PKCE`,
  'service-account': msg`Client Credentials`,
};

function isKnownKind(kind: string): kind is ClientKind {
  return (CLIENT_KINDS as readonly string[]).includes(kind);
}

/** 種別の表示名。**未知の値は生値をそのまま返す**（`—`・「不明」へ丸めない）。 */
export function clientKindLabel(kind: string): MessageDescriptor | string {
  return isKnownKind(kind) ? KIND_LABELS[kind] : kind;
}

/** 認証方式の表示名。未知の種別は空文字（存在しない方式を騙らない）。 */
export function clientAuthLabel(kind: string): MessageDescriptor | string {
  return isKnownKind(kind) ? KIND_AUTH_LABELS[kind] : '';
}

/**
 * 無人アカウントか。
 *
 * 🔴 **この判定が「ABAC 属性が必須か」と同義である**（05_screens §SC-12: 無人時必須）。
 * 2 か所へ書くと片方だけが緩むので 1 つに閉じる。
 */
export function requiresAttributes(kind: string): boolean {
  return kind === 'service-account';
}

/** 属性割当の 1 組（辞書のキーと、その許可値のひとつ）。 */
export interface AttributeEntry {
  key: string;
  value: string;
}

/**
 * 属性割当に使える辞書項目。
 *
 * **利用者スコープの属性だけを出す。** MCP クライアントは ABAC の**主体**であり、
 * 文書スコープの属性（文書側に付く値）を主体へ割り当てると意味が反転する。
 * 許可値を持たない項目も出さない（選べる値が無い項目を選択肢に置かない）。
 */
export function assignableAttributes(
  definitions: readonly AttributeDefinitionDto[],
): AttributeDefinitionDto[] {
  return definitions.filter((d) => d.scope === 'user' && d.allowedValues.length > 0);
}

/**
 * 入力された組を契約の形（キー → 値）へ畳む。
 *
 * 同じキーを 2 度積んだら**後勝ち**である（契約は 1 キー 1 値であり、集合を持てない）。
 */
export function buildAttributes(entries: readonly AttributeEntry[]): Record<string, string> {
  const attributes: Record<string, string> = {};
  for (const entry of entries) attributes[entry.key] = entry.value;
  return attributes;
}

/**
 * 登録内容が入力規則を満たすか（満たさない理由の識別子を返す。空なら妥当）。
 *
 * **文言はここへ書かない**（`@platform/ui` と同じ理由でカタログの入口を 2 つに割らない）。
 * 呼び出し側が識別子を文言へ写す。
 */
export type RegistrationIssue =
  'client-id-required' | 'display-name-required' | 'attributes-required';

export function validateRegistration(input: {
  clientId: string;
  displayName: string;
  kind: string;
  attributes: readonly AttributeEntry[];
}): RegistrationIssue[] {
  const issues: RegistrationIssue[] = [];
  if (input.clientId.trim().length === 0) issues.push('client-id-required');
  if (input.displayName.trim().length === 0) issues.push('display-name-required');
  // 05_screens §SC-12: 無人時は ABAC 属性が必須。**有人では要求しない**
  // （有人は利用者本人の属性で解決されるため、割り当てる属性が無いのが正しい）。
  if (requiresAttributes(input.kind) && input.attributes.length === 0)
    issues.push('attributes-required');
  return issues;
}
