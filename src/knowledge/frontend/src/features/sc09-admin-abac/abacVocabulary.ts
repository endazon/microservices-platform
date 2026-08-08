import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

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
