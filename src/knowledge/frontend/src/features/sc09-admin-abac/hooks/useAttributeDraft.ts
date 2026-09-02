import { useState } from 'react';
import { parseAllowedValues } from '../types/abacVocabulary';
import type { AttributeScope } from '../types/abacVocabulary';

// SC-09, UC-05, FR-09: 属性辞書の登録フォームの**クライアント状態**
// （計画 13_frontend-stack §ディレクトリ構成 の `hooks/`。IADR-0309 決定 1）。
//
// **サーバー状態をここへ持ち込まない**（登録・削除は `api/useAbacAdmin.ts` の TanStack Query）。
// 許可値の解釈は `types/abacVocabulary.ts` の `parseAllowedValues`（純関数）が持つ。
// ここが持つのは**遷移**である。
//
// 🔴 **`usePolicyDraft` と共通化しない。** 同じ feature に居る 2 つのフォームだが、
// こちらは**辞書項目そのものを新規作成する**（キーは自由入力・許可値はカンマ区切り文字列）のに対し、
// あちらは**既存の辞書項目とその許可値を選ぶ**。入力の形も遷移も別物であり、
// 束ねると「呼び出し側で分岐する 1 つのフック」になって 2 本のままより読めなくなる（IADR-0341）。

/** 属性辞書登録フォームの下書きと操作。 */
export interface AttributeDraft {
  key: string;
  setKey: (value: string) => void;
  label: string;
  setLabel: (value: string) => void;
  /** 許可値の生入力（カンマ区切り）。畳むのは `body()` の中だけである。 */
  allowedValues: string;
  setAllowedValues: (value: string) => void;
  required: boolean;
  setRequired: (value: boolean) => void;
  scope: AttributeScope;
  setScope: (value: AttributeScope) => void;
  /** キーが入っているか。**これを満たさないと登録ボタンを押せない。** */
  canSubmit: boolean;
  /** 要求本文。キーとラベルは trim し、許可値はカンマ区切りを畳む。 */
  body: () => {
    key: string;
    label: string;
    allowedValues: string[];
    required: boolean;
    scope: AttributeScope;
  };
  /**
   * 登録成功後の後始末。
   *
   * 🔴 **必須（`required`）とスコープ（`scope`）は消さない。** 管理者は同じスコープの属性を
   * 続けて足すことが多く、既定へ戻すと毎回選び直させることになる。
   */
  resetAfterCreate: () => void;
}

export function useAttributeDraft(): AttributeDraft {
  const [key, setKey] = useState('');
  const [label, setLabel] = useState('');
  const [allowedValues, setAllowedValues] = useState('');
  const [required, setRequired] = useState(false);
  const [scope, setScope] = useState<AttributeScope>('document');

  return {
    key,
    setKey,
    label,
    setLabel,
    allowedValues,
    setAllowedValues,
    required,
    setRequired,
    scope,
    setScope,
    canSubmit: key.trim().length > 0,
    body: () => ({
      key: key.trim(),
      label: label.trim(),
      allowedValues: parseAllowedValues(allowedValues),
      required,
      scope,
    }),
    resetAfterCreate: () => {
      setKey('');
      setLabel('');
      setAllowedValues('');
    },
  };
}
