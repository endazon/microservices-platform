import { describe, expect, it } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import type {
  AttributeDefinitionDto,
  PlatformUserDto,
} from '@foundation/api/generated/bff.schemas';
import { useUserPermissionEditor } from './useUserPermissionEditor';

// SC-17, UC-05, FR-05/FR-09 / IADR-0341: 権限編集の**遷移**を画面を描かずに固定する。
//
// 🔴 とくに 2 つ —— ①「対象が変わったときだけ下書きを引き直す」（一覧の再取得で入力途中を潰さない）
// ②「任意属性を空へ戻すとキーごと落ちる」（反映は差し替えなので、送らないことが外すことである）。
// どちらも画面テストでは**枝として踏みにくい**（前者は一覧の再取得を、後者は送信本文を見る必要がある）。

const USERS: PlatformUserDto[] = [
  {
    id: 'u1',
    username: 'taro',
    displayName: '山田太郎',
    enabled: true,
    roles: ['viewer'],
    attributes: { department: 'sales', clearance: 'internal' },
  },
  {
    id: 'u2',
    username: 'hanako',
    displayName: '鈴木花子',
    enabled: true,
    roles: ['admin'],
    attributes: { department: 'dev' },
  },
];

/**
 * 属性定義のフィクスチャ。
 *
 * 契約の `createdAt` / `updatedAt` は**この規則の判定に一切効かない**ので空文字で埋める。
 */
const attr = (
  a: Omit<AttributeDefinitionDto, 'createdAt' | 'updatedAt'>,
): AttributeDefinitionDto => ({ ...a, createdAt: '', updatedAt: '' });

const DEFINITIONS: AttributeDefinitionDto[] = [
  attr({
    id: 'a1',
    key: 'department',
    label: '部門',
    allowedValues: ['sales', 'dev'],
    required: true,
    scope: 'user',
  }),
  attr({
    id: 'a2',
    key: 'tag',
    label: 'タグ',
    allowedValues: ['alpha', 'beta'],
    required: false,
    scope: 'user',
  }),
];

describe('useUserPermissionEditor (SC-17)', () => {
  it('is closed until a row is opened', () => {
    const { result } = renderHook(() => useUserPermissionEditor(USERS));
    expect(result.current.editing).toBeNull();
    expect(result.current.draftRoles).toEqual([]);
  });

  it('seeds the draft from the user that was opened', () => {
    const { result } = renderHook(() => useUserPermissionEditor(USERS));
    act(() => result.current.open('u1'));

    expect(result.current.editing?.id).toBe('u1');
    expect(result.current.draftRoles).toEqual(['viewer']);
    expect(result.current.draftAttributes).toEqual({ department: 'sales', clearance: 'internal' });
  });

  it('re-seeds when the target changes, and only then', () => {
    const { result, rerender } = renderHook(({ users }) => useUserPermissionEditor(users), {
      initialProps: { users: USERS },
    });
    act(() => result.current.open('u1'));
    act(() => result.current.toggleRole('editor'));
    expect(result.current.draftRoles).toEqual(['viewer', 'editor']);

    // 🔴 一覧が入れ替わっただけでは下書きを潰さない（入力途中を毎再描画で捨てない）。
    rerender({ users: [...USERS] });
    expect(result.current.draftRoles).toEqual(['viewer', 'editor']);

    // 対象を変えたら引き直す（他の管理者の変更を握り潰さない）。
    act(() => result.current.open('u2'));
    expect(result.current.draftRoles).toEqual(['admin']);
  });

  it('toggles a role on and off', () => {
    const { result } = renderHook(() => useUserPermissionEditor(USERS));
    act(() => result.current.open('u1'));
    act(() => result.current.toggleRole('viewer'));
    expect(result.current.draftRoles).toEqual([]);
    act(() => result.current.toggleRole('viewer'));
    expect(result.current.draftRoles).toEqual(['viewer']);
  });

  it('drops the key entirely when an optional attribute is cleared', () => {
    const { result } = renderHook(() => useUserPermissionEditor(USERS));
    act(() => result.current.open('u1'));
    act(() => result.current.setAttribute('tag', 'alpha'));
    expect(result.current.draftAttributes).toEqual({
      department: 'sales',
      clearance: 'internal',
      tag: 'alpha',
    });

    // 🔴 空文字を「値」として残さない —— 反映は差し替えであり、送らないことが外すことである。
    act(() => result.current.setAttribute('tag', ''));
    expect(result.current.draftAttributes).toEqual({ department: 'sales', clearance: 'internal' });
    expect('tag' in result.current.draftAttributes).toBe(false);
  });

  it('reports the issues and refuses to submit until they are gone', () => {
    const { result } = renderHook(() => useUserPermissionEditor(USERS));
    act(() => result.current.open('u1'));
    act(() => result.current.toggleRole('viewer')); // ロールを 0 件にする
    act(() => result.current.setAttribute('department', '')); // 必須属性を外す

    let ok = true;
    act(() => {
      ok = result.current.validate(DEFINITIONS);
    });
    expect(ok).toBe(false);
    expect(result.current.issues).toEqual(['roles-required', 'required-attribute-missing']);

    act(() => result.current.toggleRole('viewer'));
    act(() => result.current.setAttribute('department', 'dev'));
    act(() => {
      ok = result.current.validate(DEFINITIONS);
    });
    expect(ok).toBe(true);
    expect(result.current.issues).toEqual([]);
  });

  it('closes the editor', () => {
    const { result } = renderHook(() => useUserPermissionEditor(USERS));
    act(() => result.current.open('u1'));
    act(() => result.current.close());
    expect(result.current.editing).toBeNull();
  });
});
