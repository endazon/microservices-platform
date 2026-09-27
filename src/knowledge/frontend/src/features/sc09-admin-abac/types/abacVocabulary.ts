import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';
import type { AttributeDefinitionDto } from '@foundation/api/generated/bff.schemas';

// SC-09, UC-05, FR-09: ABAC 管理の語彙と条件の組み立て（純関数）。
//
// 契約（AuthorizationService の `PolicyAction` / `AttributeScope`、`AbacPolicyDto`）が定める
// 値集合をそのまま写す。**判定を DOM から切り離してあるのは、値集合そのものを描画なしで
// 試験できるようにするためである** —— #503 の変異試験 M31 は「値集合から 1 値落としても
// 画面テストは 1 件も落ちない」ことを実測した（IADR-0129 決定 6）。

/** ポリシーの対象アクション（契約 `PolicyAction.All`）。 */
export const POLICY_ACTIONS = ['read', 'analyze', 'manage'] as const;

export type PolicyAction = (typeof POLICY_ACTIONS)[number];

/** 属性のスコープ（契約 `AttributeScope.All`）。条件の振り分け先を決める。 */
export const ATTRIBUTE_SCOPES = ['document', 'user'] as const;

export type AttributeScope = (typeof ATTRIBUTE_SCOPES)[number];

const ACTION_LABELS: Record<PolicyAction, MessageDescriptor> = {
  read: msg`閲覧`,
  analyze: msg`分析`,
  manage: msg`管理`,
};

const SCOPE_LABELS: Record<AttributeScope, MessageDescriptor> = {
  document: msg`文書`,
  user: msg`利用者`,
};

function isKnownAction(action: string): action is PolicyAction {
  return (POLICY_ACTIONS as readonly string[]).includes(action);
}

function isKnownScope(scope: string): scope is AttributeScope {
  return (ATTRIBUTE_SCOPES as readonly string[]).includes(scope);
}

/** アクションの表示名。未知の値は生値をそのまま返す（`—`・「不明」へ丸めない）。 */
export function policyActionLabel(action: string): MessageDescriptor | string {
  return isKnownAction(action) ? ACTION_LABELS[action] : action;
}

/** スコープの表示名。未知の値は生値をそのまま返す。 */
export function attributeScopeLabel(scope: string): MessageDescriptor | string {
  return isKnownScope(scope) ? SCOPE_LABELS[scope] : scope;
}

/**
 * SC-09, FR-09（#1609・計画 ADR-0116 決定 3）: 属性辞書の許可値の出所（契約 `allowedValuesSource`）。
 * `department` の許可値は realm の部門グループから導かれ、手で足す・消すことはできない。
 * - `realm` … 今回 realm から導いた値
 * - `realm-unavailable` … realm を読めず、最後に確かめた値を示している（**不明**）
 * - 手で持つキーは null（出所を示さない）
 */
export const ALLOWED_VALUES_SOURCES = ['realm', 'realm-unavailable'] as const;

type AllowedValuesSource = (typeof ALLOWED_VALUES_SOURCES)[number];

/** 出所の表示。**色だけに頼らない**（`StatusBadge` がアイコン＋文言を強制する）。不明は注意の色にする。 */
interface AllowedValuesSourceBadge {
  tone: 'neutral' | 'warning';
  label: MessageDescriptor | string;
}

const SOURCE_BADGES: Record<AllowedValuesSource, AllowedValuesSourceBadge> = {
  realm: { tone: 'neutral', label: msg`realm の部門グループから導出` },
  'realm-unavailable': {
    tone: 'warning',
    label: msg`不明（realm を読めないため最後に確かめた値）`,
  },
};

function isKnownSource(source: string): source is AllowedValuesSource {
  return (ALLOWED_VALUES_SOURCES as readonly string[]).includes(source);
}

/** 出所の表示。手で持つキー（null・未指定）は null。未知の値は生値をそのまま出す（「不明」へ丸めない）。 */
export function allowedValuesSourceBadge(
  source: string | null | undefined,
): AllowedValuesSourceBadge | null {
  if (source === null || source === undefined || source === '') return null;
  return isKnownSource(source) ? SOURCE_BADGES[source] : { tone: 'neutral', label: source };
}

/**
 * FR-05, SC-09, 計画 ADR-0036 D-02・D-03・D-06・D-07, ADR-0121 決定 1 (#1666): **動的束縛を置ける (action, key, 変数) の組**。
 *
 * 計画が束縛を置くのは**文書の条件の次の組だけ**である（07_abac-attribute-model §動的束縛 の判定規則）。
 * - `read` の所有者: `doc.owner ∈ { ${current_user} }`
 * - `read` の共有先: `doc.shared_with ∩ ({${current_user}} ∪ ${current_groups}) ≠ ∅`
 * - `write` の所有者: `doc.owner ∈ { ${current_user} }`（共有先には書き込み権限を与えない）
 * - `analyze`・`manage` には無い。
 *
 * 画面の対象アクションはいまのところ `read`・`analyze`・`manage` の 3 つなので、画面で選べるのは `read` の 2 つだけである
 * （`write` の行は検証器と表を揃えるために持つ）。
 *
 * 🔴 **語彙は計画が定める。** 認可サービスの検証器（`AbacValidation.DynamicBindingPositions`）が
 * 同じ表を持ち、ここに無い組は保存で 400 になる。足すなら計画 ADR の側で定義してから両方に足す。
 */
/* eslint-disable lingui/no-unlocalized-strings -- 束縛変数の記法（契約の値。表示文言ではない） */
const CURRENT_USER = '${current_user}';
const CURRENT_GROUPS = '${current_groups}';
/* eslint-enable lingui/no-unlocalized-strings */

export const DYNAMIC_BINDINGS: Readonly<
  Record<string, Readonly<Record<string, readonly string[]>>>
> = {
  read: { owner: [CURRENT_USER], shared_with: [CURRENT_USER, CURRENT_GROUPS] },
  write: { owner: [CURRENT_USER] },
};

/** 束縛変数の平易な説明。記法（`${…}`）は翻訳文へ入れない（ICU の `{}` と衝突する）。 */
const BINDING_LABELS: Record<string, MessageDescriptor> = {
  [CURRENT_USER]: msg`操作する利用者本人`,
  [CURRENT_GROUPS]: msg`操作する利用者の所属グループ`,
};

/** 辞書に無い束縛の位置へ付ける表示名（辞書に同じキーがあればそちらのラベルを使う）。 */
const BINDING_ATTRIBUTE_LABELS: Record<string, MessageDescriptor> = {
  owner: msg`所有者`,
  shared_with: msg`共有先`,
};

/** 値が計画の定める束縛変数か（`${current_user}` / `${current_groups}`。大小を区別する）。 */
export function isDynamicBinding(value: string): boolean {
  return Object.prototype.hasOwnProperty.call(BINDING_LABELS, value);
}

/** 束縛変数の説明。束縛でなければ null（呼び出し側が生値を出す）。 */
export function dynamicBindingLabel(value: string): MessageDescriptor | null {
  return isDynamicBinding(value) ? BINDING_LABELS[value] : null;
}

/** ポリシーの条件エディタの「対象属性」の 1 選択肢。 */
export interface PolicyAttributeOption {
  /**
   * `Select` の値。**スコープとキーの組**（`document:owner`）である。
   *
   * 🔴 キーだけにしない —— 辞書は同じキーを別スコープに持てる（利用者 `department` と文書 `department`）。
   * キーだけでは選び分けられず、先に並んだ方が黙って選ばれる（#1666 のレビュー: 利用者スコープの `owner` が
   * 文書の束縛の選択肢を塞いだ）。
   */
  id: string;
  key: string;
  scope: string;
  /** 表示名。辞書のラベル（無ければキー）か、束縛の位置の既定の名前。 */
  label: MessageDescriptor | string;
  /** 選べる値。辞書の許可値の後ろに、その位置で許される束縛の値が続く。 */
  values: string[];
}

/** 選択肢の識別子（`Select` の値）。 */
function policyAttributeOptionId(scope: string, key: string): string {
  return `${scope}:${key}`;
}

function ownValue<T>(table: Readonly<Record<string, T>>, key: string): T | undefined {
  return Object.prototype.hasOwnProperty.call(table, key) ? table[key] : undefined;
}

/** その action の文書の条件で、そのキーに置ける束縛。無ければ空。**キーは大小を区別する**（下の注記）。 */
export function bindingsAt(action: string, key: string): readonly string[] {
  const positions = ownValue(DYNAMIC_BINDINGS, action);
  return (positions && ownValue(positions, key)) ?? [];
}

/**
 * 条件エディタの「対象属性」の選択肢を作る（#1666）。
 *
 * - 属性辞書の属性はそのまま（値＝許可値）。**その action で文書属性が束縛の位置に当たるキー**なら、値を束縛だけにする
 *   （束縛とリテラルの混在を作らせない）。
 * - **action の次元を持つ**（#1666 監査）: `read` なら `owner`・`shared_with`、`analyze`・`manage` なら束縛の選択肢は無い。
 * - 束縛の位置のキーが辞書の**文書スコープに無い**ときは、文書属性の選択肢を足す（`owner` は利用者名、
 *   `shared_with` は利用者名・グループ ID を値に持ち、列挙できないので辞書に載らない。seed の注記）。
 *   **利用者スコープに同名の属性があっても足す**（そちらは別の選択肢として残る。識別子はスコープつき）。
 * - **利用者属性には束縛を足さない**（評価器は利用者の条件を束縛しない）。
 * - **キーは大小を区別する**（`Owner` は束縛の位置ではない）。文書の属性の突き合わせは大小を区別するので
 *   （`AttributeFilterMatch.MatchesAll` の `TryGetValue`・`WithSharedWith` の Ordinal）、`Owner ∈ {${current_user}}`
 *   は `owner` を持つ文書に一致しない。認可サービスの検証器も同じ規則で `Owner` の束縛を拒否する。
 *
 * 🔴 **自由入力の余地は作らない。** 値は常にこの配列から選ぶ。
 */
export function policyAttributeOptions(
  attributes: readonly AttributeDefinitionDto[],
  action: string,
): PolicyAttributeOption[] {
  const options: PolicyAttributeOption[] = attributes.map((a) => {
    const bindings = a.scope === 'document' ? bindingsAt(action, a.key) : [];
    // 🔴 束縛の位置では値を束縛だけにする（辞書の許可値を並べない）—— 束縛とリテラルを混ぜると、
    // 束縛の条件と無関係にそのリテラルの文書が全員に許可される（検証器も混在を拒否する）。
    const values = bindings.length > 0 ? [...bindings] : [...a.allowedValues];
    return {
      id: policyAttributeOptionId(a.scope, a.key),
      key: a.key,
      scope: a.scope,
      label: a.label || a.key,
      values,
    };
  });
  for (const [key, bindings] of Object.entries(ownValue(DYNAMIC_BINDINGS, action) ?? {})) {
    if (attributes.some((a) => a.scope === 'document' && a.key === key)) continue;
    options.push({
      id: policyAttributeOptionId('document', key),
      key,
      scope: 'document',
      label: BINDING_ATTRIBUTE_LABELS[key] ?? key,
      values: [...bindings],
    });
  }
  return options;
}

/** 条件エディタが積む 1 条件（属性のスコープ・キー・値）。 */
export interface ConditionEntry {
  scope: string;
  key: string;
  value: string;
}

/** 契約の条件表現（属性キー → 許可値の集合）。 */
export type ConditionMap = Record<string, string[]>;

export interface PolicyConditions {
  userConditions: ConditionMap;
  documentConditions: ConditionMap;
}

/**
 * 条件エディタが積んだ組を、契約の 2 つの辞書（利用者条件・文書条件）へ組み立てる。
 *
 * **契約が表現できるのは属性キー → 許可値の集合所属だけ**である
 * （`Dictionary<string, List<string>>`）。比較演算子・「含む」は表現できないため、
 * hi-fi が描く自由記述の条件式は実装しない（IADR-0129 決定 2）。
 *
 * 同じ属性へ複数の値を積むと**同じキーの配列**へまとまる（いずれかに一致＝集合所属）。
 * 同一の組を 2 度積んでも値は重複しない。
 */
export function buildConditions(entries: readonly ConditionEntry[]): PolicyConditions {
  const conditions: PolicyConditions = { userConditions: {}, documentConditions: {} };
  for (const entry of entries) {
    const bucket =
      entry.scope === 'user' ? conditions.userConditions : conditions.documentConditions;
    const values = (bucket[entry.key] ??= []);
    if (!values.includes(entry.value)) values.push(entry.value);
  }
  return conditions;
}

/** 一覧に出す条件の 1 行（スコープ・属性キー・値の集合）。 */
export interface ConditionSummaryEntry {
  scope: AttributeScope;
  key: string;
  values: string[];
}

/**
 * ポリシーの条件を一覧表示用の行へ畳む。
 *
 * 利用者条件を先に出す（計画の「利用者属性 × 文書属性」の並び）。
 * 空の辞書は行を生まない（「条件なし」＝すべてに一致することを呼び出し側が示す）。
 */
export function summarizeConditions(policy: {
  userConditions?: ConditionMap | null;
  documentConditions?: ConditionMap | null;
}): ConditionSummaryEntry[] {
  const rows: ConditionSummaryEntry[] = [];
  for (const [key, values] of Object.entries(policy.userConditions ?? {})) {
    rows.push({ scope: 'user', key, values });
  }
  for (const [key, values] of Object.entries(policy.documentConditions ?? {})) {
    rows.push({ scope: 'document', key, values });
  }
  return rows;
}

/** 許可値の入力（カンマ区切り）を配列へ。空要素は落とす。 */
export function parseAllowedValues(input: string): string[] {
  return input
    .split(',')
    .map((v) => v.trim())
    .filter((v) => v.length > 0);
}
