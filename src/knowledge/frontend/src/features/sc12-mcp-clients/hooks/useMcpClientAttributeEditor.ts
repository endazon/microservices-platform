import { useCallback, useState } from 'react';
import { buildAttributes } from '../types/mcpClientVocabulary';
import type { AttributeEntry } from '../types/mcpClientVocabulary';

// SC-12, UC-09, FR-16「無人アカウントの ABAC 属性割当」の**登録後の差し替え**が持つ
// クライアント状態（計画 13_frontend-stack §ディレクトリ構成 の `hooks/`。IADR-0309 決定 1）。
//
// 🔴 **差し替えは置換であって追加ではない。** 後段の端点は属性の集合ごと入れ替えるので、
// 編集を始めるときに**現在の値を読み込む** —— 空から始めると「変更しなかった属性が消える」。
// この規則は画面を描かずに固定できる形にしてある（`start()` の直後の `entries`）。
//
// **サーバー状態をここへ持ち込まない**（送信は `api/useMcpClients.ts` の TanStack Query）。

/** 属性差し替えフォームの下書きと操作。 */
export interface McpClientAttributeEditor {
  /** 編集中のクライアント ID。閉じているときは null。 */
  editingClientId: string | null;
  entries: AttributeEntry[];
  key: string;
  /** 属性を選び直す。**値は消える**（登録フォームと同じ規則）。 */
  selectKey: (key: string) => void;
  value: string;
  setValue: (value: string) => void;
  /**
   * 対象の現在の属性を読み込んで編集を始める。入力欄は空に戻す。
   *
   * 🔴 **参照が安定している**（`useCallback`）。一覧の列定義（`useMemo`）から呼ぶので、
   * 描画のたびに別関数になると列定義が毎回作り直される。
   */
  start: (clientId: string, current: Record<string, string>) => void;
  /** 選んでいる属性と値を積む。**キーか値が空なら何もしない／同じキーは後勝ち。** */
  addEntry: () => void;
  removeEntry: (key: string) => void;
  /**
   * 保存してよいか。
   *
   * 🔴 **空では保存させない。** 無人アカウントに属性が 1 つも無い状態は、登録時に禁じているのと
   * 同じ理由（判定軸が消える）で作らせてはならない。
   */
  canSave: boolean;
  /** 契約の形に畳んだ属性。 */
  attributes: () => Record<string, string>;
  close: () => void;
}

export function useMcpClientAttributeEditor(): McpClientAttributeEditor {
  const [editingClientId, setEditingClientId] = useState<string | null>(null);
  const [entries, setEntries] = useState<AttributeEntry[]>([]);
  const [key, setKey] = useState('');
  const [value, setValue] = useState('');

  // 参照を固定する（呼び出す先はすべて `useState` の setter で、これ自体が安定している）。
  const start = useCallback((clientId: string, current: Record<string, string>) => {
    setEditingClientId(clientId);
    setEntries(Object.entries(current).map(([k, v]) => ({ key: k, value: v })));
    setKey('');
    setValue('');
  }, []);

  return {
    editingClientId,
    entries,
    key,
    selectKey: (next: string) => {
      setKey(next);
      setValue('');
    },
    value,
    setValue,
    start,
    addEntry: () => {
      if (!key || !value) return;
      setEntries((prev) => [...prev.filter((e) => e.key !== key), { key, value }]);
      setValue('');
    },
    removeEntry: (target: string) => setEntries((prev) => prev.filter((e) => e.key !== target)),
    canSave: entries.length > 0,
    attributes: () => buildAttributes(entries),
    close: () => setEditingClientId(null),
  };
}
