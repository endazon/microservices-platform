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
  | 'client-id-required'
  | 'display-name-required'
  | 'attributes-required'
  | 'redirect-uris-required'
  | 'redirect-uris-too-many'
  | 'redirect-uri-invalid'
  | 'redirect-uri-duplicate';

/**
 * 有人（対話型）か。**この判定が「リダイレクト URI が必須か」と同義である**（05_screens §SC-12 の入力表:
 * 有人時必須。ADR-0134 決定 1）。無人には送らない（後段は無人に渡されたら 400 で拒む）。
 */
export function requiresRedirectUris(kind: string): boolean {
  return kind === 'interactive';
}

/** 1 クライアントに登録できるリダイレクト URI の上限（後段の `RedirectUriRules.MaxCount` と同じ値）。 */
const MAX_REDIRECT_URIS = 10;

/** 入力欄（1 行 1 件）を URI の並びへ畳む。前後の空白と空行は落とす。 */
export function parseRedirectUris(text: string): string[] {
  return text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

/**
 * ループバック（port は任意。書くなら 1〜5 桁の数字の後が `/`・`?`・末尾）。後段 `RedirectUriRules` と同じ綴りの判定。
 * `new URL` は既定の port（`:80`）を落とすので、port の有無は綴りで見る。`:` だけ（`http://127.0.0.1:/cb`）は一致しない＝不正。
 */
const LOOPBACK = /^http:\/\/(127\.0\.0\.1|\[::1\])(?::(\d{1,5}))?([/?]|$)/;

/**
 * リダイレクト URI 1 件が規則を満たすか（ADR-0134 決定 1）。
 *
 * `https` の URI か、ループバックの `http://127.0.0.1` / `http://[::1]`（RFC 8252。port は任意）だけを許す。
 * ワイルドカード（`*`）・フラグメント・利用者情報・`localhost` は不可。port を書くなら 1〜65535。
 *
 * ［2026-10-09］port なしのループバックを許す（実行時の任意の port で戻す。ネイティブのクライアントは起動のたびに port が変わる）。
 * 当初は port の明示を必須にしていた（CVE-2024-8883: 旧い認可サーバーは port なしで登録された `http://127.0.0.1/cb` に
 * `http://127.0.0.1:49152@evil.example/cb` を一致させ、認可コードを外へ送った）。修正済みの版へ上げ、横取りの形は認可サーバーの照合が拒む
 * （結合スタックの門が日次で測る）。利用者情報の拒否は残す —— 横取りの形そのものをここでも止める。
 *
 * 🔴 **最終の判定は後段（`RedirectUriRules`）が持つ。** ここは送る前の写しであり、食い違えば後段の 400 が
 * 理由を名指しして返る（画面はそれをそのまま出す）。
 */
export function isAllowedRedirectUri(value: string): boolean {
  if (value.length === 0 || value.length > 2048) return false;
  if (value.includes('*') || value.includes('#') || !value.includes('://')) return false;
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return false;
  }
  if (url.username !== '' || url.password !== '') return false;
  if (url.protocol === 'https:') return true;
  if (url.protocol !== 'http:') return false;
  // 綴りで判定する（`localhost`・`127.1` のような別の綴りを通さない。後段も綴りで照合する）。
  const match = LOOPBACK.exec(value);
  if (match === null) return false;
  const port = match[2];
  return port === undefined || (Number(port) > 0 && Number(port) <= 65535);
}

export function validateRegistration(input: {
  clientId: string;
  displayName: string;
  kind: string;
  attributes: readonly AttributeEntry[];
  redirectUris?: readonly string[];
}): RegistrationIssue[] {
  const issues: RegistrationIssue[] = [];
  if (input.clientId.trim().length === 0) issues.push('client-id-required');
  if (input.displayName.trim().length === 0) issues.push('display-name-required');
  // 05_screens §SC-12: 無人時は ABAC 属性が必須。**有人では要求しない**
  // （有人は利用者本人の属性で解決されるため、割り当てる属性が無いのが正しい）。
  if (requiresAttributes(input.kind) && input.attributes.length === 0)
    issues.push('attributes-required');
  // 05_screens §SC-12（2026-10-09 追加）: 有人時はリダイレクト URI が必須（完全一致で照合）。
  if (requiresRedirectUris(input.kind)) {
    const uris = input.redirectUris ?? [];
    if (uris.length === 0) issues.push('redirect-uris-required');
    else if (uris.length > MAX_REDIRECT_URIS) issues.push('redirect-uris-too-many');
    if (uris.some((uri) => !isAllowedRedirectUri(uri))) issues.push('redirect-uri-invalid');
    if (new Set(uris).size !== uris.length) issues.push('redirect-uri-duplicate');
  }
  return issues;
}
