import { describe, it, expect } from 'vitest';
import { i18n } from '@foundation/i18n';
import {
  ATTRIBUTE_SCOPES,
  attributeScopeLabel,
  buildConditions,
  parseAllowedValues,
  policyActionLabel,
  POLICY_ACTIONS,
  summarizeConditions,
} from './abacVocabulary';

// SC-09, UC-05, FR-09: ABAC 管理の語彙と条件の組み立て（純関数）。
//
// **値集合を純関数テストで固定する**理由（IADR-0129 決定 6）: #503 の変異試験 M31 は
// 「値集合から 1 値落としても画面テストは 1 件も落ちない」ことを実測した。選択肢の数を
// 数えているテストが無いかぎり、`manage` が消えても画面テストは全部通る。

function text(label: ReturnType<typeof policyActionLabel>): string {
  return typeof label === 'string' ? label : i18n._(label);
}

describe('abacVocabulary (SC-09)', () => {
  // 契約 `PolicyAction.All` の 3 値。
  it('fixes exactly the three policy actions the contract defines', () => {
    expect([...POLICY_ACTIONS]).toEqual(['read', 'analyze', 'manage']);
  });

  // 契約 `AttributeScope.All` の 2 値。条件の振り分け先を決める。
  it('fixes exactly the two attribute scopes the contract defines', () => {
    expect([...ATTRIBUTE_SCOPES]).toEqual(['document', 'user']);
  });

  it.each([
    ['read', '閲覧'],
    ['analyze', '分析'],
    ['manage', '管理'],
  ])('maps the action %s to a readable label', (action, label) => {
    expect(text(policyActionLabel(action))).toBe(label);
  });

  it.each([
    ['document', '文書'],
    ['user', '利用者'],
  ])('maps the scope %s to a readable label', (scope, label) => {
    expect(text(attributeScopeLabel(scope))).toBe(label);
  });

  // 未知の値を握り潰さない（`—` や「不明」へ丸めない）。契約が 4 つ目を返したら画面で気付ける。
  it('shows an unknown action or scope verbatim instead of hiding it', () => {
    expect(policyActionLabel('export')).toBe('export');
    expect(attributeScopeLabel('tenant')).toBe('tenant');
  });

  // IADR-0129 決定 2: 契約が表現するのは**属性キー → 許可値の集合所属**だけである。
  // 条件エディタが積んだ組を、スコープごとの 2 つの辞書へ振り分ける。
  it('splits the accumulated conditions into user and document buckets by scope', () => {
    expect(
      buildConditions([
        { scope: 'user', key: 'dept', value: '経理' },
        { scope: 'document', key: 'confidentiality', value: 'internal' },
      ]),
    ).toEqual({
      userConditions: { dept: ['経理'] },
      documentConditions: { confidentiality: ['internal'] },
    });
  });

  // 同じ属性へ複数の値を積むと同じキーの配列へまとまる（いずれかに一致＝集合所属）。
  it('groups multiple values of the same attribute into one set', () => {
    expect(
      buildConditions([
        { scope: 'user', key: 'dept', value: '経理' },
        { scope: 'user', key: 'dept', value: '開発' },
      ]).userConditions,
    ).toEqual({ dept: ['経理', '開発'] });
  });

  // 同じ組を 2 度積んでも値は重複しない（サーバへ同じ値を 2 つ送らない）。
  it('does not duplicate a value that is added twice', () => {
    expect(
      buildConditions([
        { scope: 'document', key: 'tags', value: '設計' },
        { scope: 'document', key: 'tags', value: '設計' },
      ]).documentConditions,
    ).toEqual({ tags: ['設計'] });
  });

  it('produces two empty maps when nothing is accumulated', () => {
    expect(buildConditions([])).toEqual({ userConditions: {}, documentConditions: {} });
  });

  // 一覧の条件列は「利用者属性 × 文書属性」の並びで畳む（計画 §SC-09 §主要素 の並び）。
  it('summarises a policy with the user conditions first', () => {
    expect(
      summarizeConditions({
        userConditions: { dept: ['経理'] },
        documentConditions: { confidentiality: ['internal', 'public'] },
      }),
    ).toEqual([
      { scope: 'user', key: 'dept', values: ['経理'] },
      { scope: 'document', key: 'confidentiality', values: ['internal', 'public'] },
    ]);
  });

  it('summarises a policy without conditions as an empty list', () => {
    expect(summarizeConditions({})).toEqual([]);
  });

  it('parses the comma separated allowed values and drops the blanks', () => {
    expect(parseAllowedValues(' public , internal ,, confidential ')).toEqual([
      'public',
      'internal',
      'confidential',
    ]);
    expect(parseAllowedValues('')).toEqual([]);
  });
});
