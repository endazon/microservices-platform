import { describe, expect, it } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { useAttributeDraft } from './useAttributeDraft';

// SC-09, UC-05, FR-09 / IADR-0341: 属性辞書登録フォームの**遷移**を画面を描かずに固定する。

describe('useAttributeDraft (SC-09)', () => {
  it('refuses to submit until the key has a non-blank value', () => {
    const { result } = renderHook(() => useAttributeDraft());
    expect(result.current.canSubmit).toBe(false);
    act(() => result.current.setKey('   '));
    expect(result.current.canSubmit).toBe(false);
    act(() => result.current.setKey('clearance'));
    expect(result.current.canSubmit).toBe(true);
  });

  it('trims the key and label and folds the comma separated allowed values', () => {
    const { result } = renderHook(() => useAttributeDraft());
    act(() => result.current.setKey('  clearance  '));
    act(() => result.current.setLabel('  機密区分  '));
    act(() => result.current.setAllowedValues(' public , internal ,, '));
    act(() => result.current.setRequired(true));
    act(() => result.current.setScope('user'));

    expect(result.current.body()).toEqual({
      key: 'clearance',
      label: '機密区分',
      allowedValues: ['public', 'internal'],
      required: true,
      scope: 'user',
    });
  });

  it('defaults to a non-required document attribute', () => {
    const { result } = renderHook(() => useAttributeDraft());
    expect(result.current.required).toBe(false);
    expect(result.current.scope).toBe('document');
  });

  it('keeps the required flag and the scope after a successful create', () => {
    const { result } = renderHook(() => useAttributeDraft());
    act(() => result.current.setKey('clearance'));
    act(() => result.current.setLabel('機密区分'));
    act(() => result.current.setAllowedValues('public,internal'));
    act(() => result.current.setRequired(true));
    act(() => result.current.setScope('user'));

    act(() => result.current.resetAfterCreate());

    expect(result.current.key).toBe('');
    expect(result.current.label).toBe('');
    expect(result.current.allowedValues).toBe('');
    // 🔴 必須とスコープは残る。同じスコープの属性を続けて足す管理者に毎回選び直させない。
    expect(result.current.required).toBe(true);
    expect(result.current.scope).toBe('user');
  });
});
