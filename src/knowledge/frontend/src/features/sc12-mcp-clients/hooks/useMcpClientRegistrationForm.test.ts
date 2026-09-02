import { describe, expect, it } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import type { AttributeDefinitionDto } from '@foundation/api/generated/bff.schemas';
import { useMcpClientRegistrationForm } from './useMcpClientRegistrationForm';

// SC-12, UC-09, FR-16 / IADR-0341: 登録フォームの**遷移**を画面を描かずに固定する。
//
// 🔴 **画面テスト（McpClientManagementPage.test.tsx）と役割が違う。** あちらが確かめるのは
// 「どの要素がどう見えるか」であり、ここが確かめるのは「どの操作が何を消し、何を残すか」である。
// 従前この規則は画面全体を描かないと踏めず、**踏んでいない枝もあった**（属性キーの選び直し・
// 登録成功後に種別が残ること）。

/**
 * 属性定義のフィクスチャ。
 *
 * 契約の `createdAt` / `updatedAt` は**この規則の判定に一切効かない**ので空文字で埋める
 * （1 件ずつ書くと、意味を持たない 2 行がフィクスチャの半分を占める）。
 */
const attr = (
  a: Omit<AttributeDefinitionDto, 'createdAt' | 'updatedAt'>,
): AttributeDefinitionDto => ({ ...a, createdAt: '', updatedAt: '' });

const DICTIONARY: AttributeDefinitionDto[] = [
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
    key: 'department',
    label: '部門',
    allowedValues: ['sales', 'dev'],
    required: false,
    scope: 'user',
  }),
  // 🔴 陰性対照: 文書スコープの属性は割当の選択肢に出ない（主体へ割り当てると意味が反転する）。
  attr({
    id: 'a3',
    key: 'sensitivity',
    label: '機微度',
    allowedValues: ['low'],
    required: false,
    scope: 'document',
  }),
  // 🔴 陰性対照: 許可値を持たない項目も出ない（選べる値が無い項目を選択肢に置かない）。
  attr({ id: 'a4', key: 'empty', label: '空', allowedValues: [], required: false, scope: 'user' }),
];

function setup() {
  return renderHook(() => useMcpClientRegistrationForm(DICTIONARY));
}

describe('useMcpClientRegistrationForm (SC-12)', () => {
  it('offers only user-scoped attributes that have allowed values', () => {
    const { result } = setup();
    expect(result.current.definitions.map((d) => d.key)).toEqual(['clearance', 'department']);
  });

  it('clears the chosen value when the attribute key changes', () => {
    const { result } = setup();
    act(() => result.current.selectAttributeKey('clearance'));
    act(() => result.current.setAttributeValue('internal'));
    expect(result.current.attributeValue).toBe('internal');

    // 別の属性へ移ったら、前の属性の許可値を持ち越さない（`department` に `internal` は無い）。
    act(() => result.current.selectAttributeKey('department'));
    expect(result.current.attributeValue).toBe('');
    expect(result.current.selectedDefinition?.key).toBe('department');
  });

  it('does not stack an entry while the key or the value is empty', () => {
    const { result } = setup();
    act(() => result.current.addEntry());
    expect(result.current.entries).toEqual([]);

    act(() => result.current.selectAttributeKey('clearance'));
    act(() => result.current.addEntry()); // 値がまだ空
    expect(result.current.entries).toEqual([]);
  });

  it('keeps one entry per key (the last write wins) and clears the value input', () => {
    const { result } = setup();
    act(() => result.current.selectAttributeKey('clearance'));
    act(() => result.current.setAttributeValue('public'));
    act(() => result.current.addEntry());
    act(() => result.current.setAttributeValue('internal'));
    act(() => result.current.addEntry());

    // 契約は 1 キー 1 値であり、集合を持てない。
    expect(result.current.entries).toEqual([{ key: 'clearance', value: 'internal' }]);
    expect(result.current.attributeValue).toBe('');
  });

  it('reports the input issues and refuses to submit until they are gone', () => {
    const { result } = setup();
    let ok = true;
    act(() => {
      ok = result.current.validate();
    });
    expect(ok).toBe(false);
    expect(result.current.issues).toEqual(['client-id-required', 'display-name-required']);

    act(() => result.current.setClientId(' agent-1 '));
    act(() => result.current.setDisplayName('エージェント'));
    act(() => {
      ok = result.current.validate();
    });
    expect(ok).toBe(true);
    expect(result.current.issues).toEqual([]);
  });

  it('requires attributes only for the unattended kind', () => {
    const { result } = setup();
    act(() => result.current.setClientId('agent-1'));
    act(() => result.current.setDisplayName('エージェント'));
    expect(result.current.needsAttributes).toBe(false);

    act(() => result.current.setKind('service-account'));
    expect(result.current.needsAttributes).toBe(true);
    act(() => {
      result.current.validate();
    });
    expect(result.current.issues).toEqual(['attributes-required']);
  });

  it('trims the body and omits attributes for the attended kind', () => {
    const { result } = setup();
    act(() => result.current.setClientId('  agent-1  '));
    act(() => result.current.setDisplayName('  エージェント  '));
    act(() => result.current.selectAttributeKey('clearance'));
    act(() => result.current.setAttributeValue('internal'));
    act(() => result.current.addEntry());

    // 有人には属性を送らない（送る値が無いのが正しい）——積んであっても本文へ載せない。
    expect(result.current.body()).toEqual({
      clientId: 'agent-1',
      displayName: 'エージェント',
      kind: 'interactive',
    });

    act(() => result.current.setKind('service-account'));
    expect(result.current.body()).toEqual({
      clientId: 'agent-1',
      displayName: 'エージェント',
      kind: 'service-account',
      attributes: { clearance: 'internal' },
    });
  });

  it('keeps the client kind after a successful registration', () => {
    const { result } = setup();
    act(() => result.current.setKind('service-account'));
    act(() => result.current.setClientId('agent-1'));
    act(() => result.current.setDisplayName('エージェント'));
    act(() => result.current.selectAttributeKey('clearance'));
    act(() => result.current.setAttributeValue('internal'));
    act(() => result.current.addEntry());

    act(() => result.current.resetAfterRegister());

    expect(result.current.clientId).toBe('');
    expect(result.current.displayName).toBe('');
    expect(result.current.entries).toEqual([]);
    // 🔴 種別は残る。同じ種別を続けて登録する管理者に毎回選び直させない。
    expect(result.current.kind).toBe('service-account');
  });
});
