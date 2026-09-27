import { describe, it, expect } from 'vitest';
import { i18n } from '@foundation/i18n';
import type { AttributeDefinitionDto } from '@foundation/api/generated/bff.schemas';
import {
  ALLOWED_VALUES_SOURCES,
  allowedValuesSourceBadge,
  ATTRIBUTE_SCOPES,
  attributeScopeLabel,
  buildConditions,
  DYNAMIC_BINDINGS,
  dynamicBindingLabel,
  isDynamicBinding,
  parseAllowedValues,
  policyAttributeOptions,
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

  // SC-09（#1609・計画 ADR-0116 決定 3）: 許可値の出所。手で持つキーは表示なし、不明は注意の色、未知の値は生値。
  it('describes the allowed-values source, flagging an unreadable realm as a warning', () => {
    expect([...ALLOWED_VALUES_SOURCES]).toEqual(['realm', 'realm-unavailable']);
    expect(allowedValuesSourceBadge(null)).toBeNull();
    expect(allowedValuesSourceBadge(undefined)).toBeNull();

    const realm = allowedValuesSourceBadge('realm');
    expect(realm?.tone).toBe('neutral');
    expect(text(realm!.label)).toBe('realm の部門グループから導出');

    const unknown = allowedValuesSourceBadge('realm-unavailable');
    expect(unknown?.tone).toBe('warning');
    expect(text(unknown!.label)).toBe('不明（realm を読めないため最後に確かめた値）');

    expect(allowedValuesSourceBadge('ldap')).toEqual({ tone: 'neutral', label: 'ldap' });
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

// 束縛変数の記法（契約の値）。
const CU = '${current_user}';
const CG = '${current_groups}';

const attr = (
  a: Omit<AttributeDefinitionDto, 'createdAt' | 'updatedAt' | 'id' | 'required'>,
): AttributeDefinitionDto => ({ ...a, id: a.key, required: false, createdAt: '', updatedAt: '' });

// FR-05, SC-09, ADR-0036 D-02・D-03・D-06, ADR-0121 決定 1 (#1666): 動的束縛の語彙（P11〜P13）。
describe('dynamic bindings (SC-09 / #1666)', () => {
  // P11: 位置と変数の表は計画の写しであり、サーバの検証器（AbacValidation）と同じ表である。
  // 1 値でも増減すれば、画面が「保存で 400 になる選択肢」を出すか、作れるはずの形を作れなくなる。
  it('fixes exactly the two binding positions the plan defines', () => {
    expect(DYNAMIC_BINDINGS).toEqual({ owner: [CU], shared_with: [CU, CG] });
  });

  // P12: 変数は 2 つだけ（D-03）。綴り違い・大小違いは束縛ではない（生値のまま出す）。
  it('recognises only the two variables, case-sensitively', () => {
    expect(isDynamicBinding(CU)).toBe(true);
    expect(isDynamicBinding(CG)).toBe(true);
    for (const v of ['${current_usr}', '${Current_User}', 'current_user', 'internal']) {
      expect(isDynamicBinding(v)).toBe(false);
      expect(dynamicBindingLabel(v)).toBeNull();
    }
    expect(i18n._(dynamicBindingLabel(CU)!)).toBe('操作する利用者本人');
    expect(i18n._(dynamicBindingLabel(CG)!)).toBe('操作する利用者の所属グループ');
  });

  // P13: 選択肢は辞書 ＋ 束縛の位置。辞書に無い owner・shared_with は文書属性として足し、値は束縛だけ。
  it('adds owner and shared_with as document attributes whose values are bindings only', () => {
    const options = policyAttributeOptions([
      attr({ key: 'clearance', label: '取扱区分', allowedValues: ['internal'], scope: 'user' }),
      attr({
        key: 'confidentiality',
        label: '機密区分',
        allowedValues: ['public'],
        scope: 'document',
      }),
    ]);
    expect(options.map((o) => [o.key, o.scope, o.values])).toEqual([
      ['clearance', 'user', ['internal']],
      // 🔴 辞書の属性には束縛を足さない（計画が束縛を置かない位置）。
      ['confidentiality', 'document', ['public']],
      ['owner', 'document', [CU]],
      ['shared_with', 'document', [CU, CG]],
    ]);
    expect(options.map((o) => text(o.label))).toEqual(['取扱区分', '機密区分', '所有者', '共有先']);
  });

  // P13: 辞書に同じキーの文書属性があれば、その許可値の後ろへ束縛を足す（重複させない。選択肢は 1 つ）。
  it('merges the bindings into a dictionary-defined document attribute without duplicating it', () => {
    const options = policyAttributeOptions([
      attr({
        key: 'shared_with',
        label: '共有先（辞書）',
        allowedValues: ['group-sales', CU],
        scope: 'document',
      }),
    ]);
    expect(options.filter((o) => o.key === 'shared_with')).toHaveLength(1);
    expect(options.find((o) => o.key === 'shared_with')!.values).toEqual(['group-sales', CU, CG]);
  });

  // P13: 利用者属性には束縛を足さない（評価器は利用者の条件を束縛しない）。同名の利用者属性があるときは
  // 選択肢を重ねない（キーが Select の値なので、重ねると選び分けられない）。
  it('never offers bindings on user attributes', () => {
    const options = policyAttributeOptions([
      attr({ key: 'owner', label: '所有者（利用者）', allowedValues: ['x'], scope: 'user' }),
    ]);
    expect(options.filter((o) => o.key === 'owner')).toEqual([
      { key: 'owner', scope: 'user', label: '所有者（利用者）', values: ['x'] },
    ]);
  });
});
