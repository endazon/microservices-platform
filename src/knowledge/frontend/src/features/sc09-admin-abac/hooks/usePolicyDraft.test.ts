import { describe, expect, it } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import type { AttributeDefinitionDto } from '@foundation/api/generated/bff.schemas';
import { usePolicyDraft } from './usePolicyDraft';

// SC-09, UC-05, FR-09 / IADR-0341: ポリシー登録フォームの**遷移**を画面を描かずに固定する。
//
// 🔴 とくに「条件の scope は属性定義から採る」——フォームは scope を持たない。
// 利用者属性か文書属性かは辞書が決めることで、画面が選び直せてはならない。
// 画面テストからは送信本文の中身までしか見えず、**なぜその scope になるか**は枝に残らない。

/**
 * 属性定義のフィクスチャ。
 *
 * 契約の `createdAt` / `updatedAt` は**この規則の判定に一切効かない**ので空文字で埋める。
 */
const attr = (
  a: Omit<AttributeDefinitionDto, 'createdAt' | 'updatedAt'>,
): AttributeDefinitionDto => ({ ...a, createdAt: '', updatedAt: '' });

const ATTRIBUTES: AttributeDefinitionDto[] = [
  attr({
    id: 'a1',
    key: 'clearance',
    label: '機密区分',
    allowedValues: ['public', 'internal'],
    required: true,
    scope: 'user',
  }),
  attr({
    id: 'a2',
    key: 'sensitivity',
    label: '機微度',
    allowedValues: ['low', 'high'],
    required: false,
    scope: 'document',
  }),
];

const setup = () => renderHook(() => usePolicyDraft(ATTRIBUTES));

describe('usePolicyDraft (SC-09)', () => {
  it('starts empty and refuses to submit without a name', () => {
    const { result } = setup();
    expect(result.current.canSubmit).toBe(false);
    act(() => result.current.setName('   '));
    expect(result.current.canSubmit).toBe(false);
    act(() => result.current.setName('社外秘の閲覧'));
    expect(result.current.canSubmit).toBe(true);
  });

  it('clears the condition value when the attribute changes', () => {
    const { result } = setup();
    act(() => result.current.selectAttributeKey('clearance'));
    act(() => result.current.setConditionValue('internal'));
    expect(result.current.values).toEqual(['public', 'internal']);

    act(() => result.current.selectAttributeKey('sensitivity'));
    expect(result.current.conditionValue).toBe('');
    expect(result.current.values).toEqual(['low', 'high']);
  });

  it('exposes no allowed values while nothing is selected', () => {
    const { result } = setup();
    expect(result.current.selected).toBeUndefined();
    expect(result.current.values).toEqual([]);
  });

  it('does not stack a condition without a selected attribute or value', () => {
    const { result } = setup();
    act(() => result.current.addCondition());
    expect(result.current.conditions).toEqual([]);

    act(() => result.current.selectAttributeKey('clearance'));
    act(() => result.current.addCondition()); // 値がまだ空
    expect(result.current.conditions).toEqual([]);
  });

  it('takes the scope from the attribute definition, not from the form', () => {
    const { result } = setup();
    act(() => result.current.selectAttributeKey('clearance'));
    act(() => result.current.setConditionValue('internal'));
    act(() => result.current.addCondition());
    act(() => result.current.selectAttributeKey('sensitivity'));
    act(() => result.current.setConditionValue('high'));
    act(() => result.current.addCondition());

    expect(result.current.conditions).toEqual([
      { scope: 'user', key: 'clearance', value: 'internal' },
      { scope: 'document', key: 'sensitivity', value: 'high' },
    ]);
    // 畳んだ本文でも 2 つの辞書に分かれる。
    act(() => result.current.setName('社外秘の閲覧'));
    expect(result.current.body()).toEqual({
      name: '社外秘の閲覧',
      action: 'read',
      userConditions: { clearance: ['internal'] },
      documentConditions: { sensitivity: ['high'] },
    });
  });

  it('removes a condition by position (the same attribute can be stacked twice)', () => {
    const { result } = setup();
    act(() => result.current.selectAttributeKey('clearance'));
    act(() => result.current.setConditionValue('public'));
    act(() => result.current.addCondition());
    act(() => result.current.setConditionValue('internal'));
    act(() => result.current.addCondition());
    expect(result.current.conditions).toHaveLength(2);

    act(() => result.current.removeCondition(0));
    expect(result.current.conditions).toEqual([
      { scope: 'user', key: 'clearance', value: 'internal' },
    ]);
  });

  it('trims the name and sends the same body for save and for validation', () => {
    const { result } = setup();
    act(() => result.current.setName('  社外秘の閲覧  '));
    act(() => result.current.setAction('manage'));
    // 🔴 保存と検証で同じものを送る（ズレる余地を作らない）——同じ呼び出しが同じ値を返す。
    expect(result.current.body()).toEqual(result.current.body());
    expect(result.current.body().name).toBe('社外秘の閲覧');
    expect(result.current.body().action).toBe('manage');
  });

  it('keeps the action after a successful save', () => {
    const { result } = setup();
    act(() => result.current.setName('社外秘の閲覧'));
    act(() => result.current.setAction('analyze'));
    act(() => result.current.selectAttributeKey('clearance'));
    act(() => result.current.setConditionValue('internal'));
    act(() => result.current.addCondition());

    act(() => result.current.resetAfterSave());

    expect(result.current.name).toBe('');
    expect(result.current.conditions).toEqual([]);
    // 🔴 アクションは残る。同じアクションのポリシーを続けて足す管理者に毎回選び直させない。
    expect(result.current.action).toBe('analyze');
  });
  // FR-05, SC-09, ADR-0121 決定 1 (#1666): 所有者の read ポリシーを選択だけで組める。
  it('builds the owner read policy from the binding position (no dictionary entry needed)', () => {
    const { result } = setup();
    act(() => result.current.setName('所有者は自分の文書を読める'));
    act(() => result.current.selectAttributeKey('owner'));
    expect(result.current.selected?.scope).toBe('document');
    expect(result.current.values).toEqual(['${current_user}']);
    act(() => result.current.setConditionValue('${current_user}'));
    act(() => result.current.addCondition());

    expect(result.current.body()).toEqual({
      name: '所有者は自分の文書を読める',
      action: 'read',
      userConditions: {},
      documentConditions: { owner: ['${current_user}'] },
    });
  });

  // 🔴 #1666: 自由入力の余地を下書きの側でも塞ぐ —— 選択肢に無い値は積まない。
  it('refuses to stack a value that is not one of the offered choices', () => {
    const { result } = setup();
    act(() => result.current.selectAttributeKey('owner'));
    act(() => result.current.setConditionValue('alice'));
    act(() => result.current.addCondition());
    act(() => result.current.selectAttributeKey('sensitivity'));
    act(() => result.current.setConditionValue('${current_user}'));
    act(() => result.current.addCondition());

    expect(result.current.conditions).toEqual([]);
  });
});
