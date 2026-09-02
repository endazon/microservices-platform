import { describe, expect, it } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { useMcpClientAttributeEditor } from './useMcpClientAttributeEditor';

// SC-12, UC-09, FR-16 / IADR-0341: 属性差し替えの**遷移**を画面を描かずに固定する。
//
// 🔴 中心は「差し替えは置換であって追加ではない」である —— 空から始めると、変更しなかった属性が
// 黙って消える。画面テストでは「開いた直後に現在値が並んでいる」ことしか見えず、
// **なぜそうでなければならないか**が枝として残らない。

describe('useMcpClientAttributeEditor (SC-12)', () => {
  it('is closed until an edit starts', () => {
    const { result } = renderHook(() => useMcpClientAttributeEditor());
    expect(result.current.editingClientId).toBeNull();
    expect(result.current.entries).toEqual([]);
    expect(result.current.canSave).toBe(false);
  });

  it('seeds the draft from the current attributes (replacement, not append)', () => {
    const { result } = renderHook(() => useMcpClientAttributeEditor());
    act(() => result.current.start('agent-1', { clearance: 'internal', department: 'dev' }));

    expect(result.current.editingClientId).toBe('agent-1');
    expect(result.current.entries).toEqual([
      { key: 'clearance', value: 'internal' },
      { key: 'department', value: 'dev' },
    ]);
    // 入力欄は空に戻す（前の編集の選択を持ち越さない）。
    expect(result.current.key).toBe('');
    expect(result.current.value).toBe('');
  });

  it('clears the chosen value when the attribute key changes', () => {
    const { result } = renderHook(() => useMcpClientAttributeEditor());
    act(() => result.current.start('agent-1', {}));
    act(() => result.current.selectKey('clearance'));
    act(() => result.current.setValue('internal'));
    act(() => result.current.selectKey('department'));
    expect(result.current.value).toBe('');
  });

  it('does not stack an entry while the key or the value is empty', () => {
    const { result } = renderHook(() => useMcpClientAttributeEditor());
    act(() => result.current.start('agent-1', {}));
    act(() => result.current.addEntry());
    expect(result.current.entries).toEqual([]);

    act(() => result.current.selectKey('clearance'));
    act(() => result.current.addEntry()); // 値がまだ空
    expect(result.current.entries).toEqual([]);
  });

  it('keeps one entry per key (the last write wins) and can drop one', () => {
    const { result } = renderHook(() => useMcpClientAttributeEditor());
    act(() => result.current.start('agent-1', { clearance: 'public' }));
    act(() => result.current.selectKey('clearance'));
    act(() => result.current.setValue('internal'));
    act(() => result.current.addEntry());
    expect(result.current.entries).toEqual([{ key: 'clearance', value: 'internal' }]);
    expect(result.current.attributes()).toEqual({ clearance: 'internal' });

    act(() => result.current.removeEntry('clearance'));
    expect(result.current.entries).toEqual([]);
  });

  it('refuses to save an empty attribute set', () => {
    const { result } = renderHook(() => useMcpClientAttributeEditor());
    act(() => result.current.start('agent-1', { clearance: 'internal' }));
    expect(result.current.canSave).toBe(true);

    // 🔴 無人アカウントに属性が 1 つも無い状態は、登録時に禁じているのと同じ理由で作らせない。
    act(() => result.current.removeEntry('clearance'));
    expect(result.current.canSave).toBe(false);
  });

  it('closes without touching the draft that was being edited', () => {
    const { result } = renderHook(() => useMcpClientAttributeEditor());
    act(() => result.current.start('agent-1', { clearance: 'internal' }));
    act(() => result.current.close());
    expect(result.current.editingClientId).toBeNull();
  });
});
