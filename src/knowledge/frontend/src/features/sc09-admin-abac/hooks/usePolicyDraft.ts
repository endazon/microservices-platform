import { useState } from 'react';
import type { AttributeDefinitionDto } from '@foundation/api/generated/bff.schemas';
import { buildConditions } from '../types/abacVocabulary';
import type { ConditionEntry, PolicyAction, PolicyConditions } from '../types/abacVocabulary';

// SC-09, UC-05, FR-09: ポリシー登録フォームの**クライアント状態**
// （計画 13_frontend-stack §ディレクトリ構成 の `hooks/`。IADR-0309 決定 1）。
//
// **サーバー状態をここへ持ち込まない** —— 保存も dry-run 検証も `api/useAbacAdmin.ts` の
// TanStack Query が持つ（ADR-0031）。**検証の判定は後段が行う**（IADR-0129。画面でローカルに
// 判定すると「検証は通ったのに保存で矛盾が出る」形になり、検証ボタンへの信頼が失われる）。
// ここに在るのは「まだ送っていない下書き」だけである。
//
// 条件の畳み込みそのものは `types/abacVocabulary.ts` の `buildConditions`（純関数）が持つ。
// ここが持つのは**遷移**である。

/** ポリシー登録フォームの下書きと操作。 */
export interface PolicyDraft {
  name: string;
  setName: (value: string) => void;
  action: PolicyAction;
  setAction: (value: PolicyAction) => void;
  conditions: ConditionEntry[];
  attributeKey: string;
  /** 対象属性を選び直す。**条件の値は消える** —— 別の属性の許可値を持ち越さない。 */
  selectAttributeKey: (key: string) => void;
  conditionValue: string;
  setConditionValue: (value: string) => void;
  /** いま選んでいる属性の定義（許可値の一覧を引くため）。未選択なら undefined。 */
  selected: AttributeDefinitionDto | undefined;
  /** 選んでいる属性の許可値。未選択なら空配列。 */
  values: string[];
  /**
   * 条件を積む。**属性が選ばれていない／値が空なら何もしない。**
   *
   * 🔴 **scope は属性定義から採る**（フォームは scope を持たない）。利用者属性か文書属性かは
   * 辞書が決めることであり、画面が選び直せてはならない。
   */
  addCondition: () => void;
  /** 積んだ条件を位置で外す（同じ属性を違う値で複数積めるため、キーでは特定できない）。 */
  removeCondition: (index: number) => void;
  /** 名前が入っているか。**保存も検証も、これを満たさないと押せない。** */
  canSubmit: boolean;
  /** 要求本文。🔴 **保存と検証で同じものを送る**（ズレる余地を作らない）。 */
  body: () => { name: string; action: PolicyAction } & PolicyConditions;
  /**
   * 保存成功後の後始末。
   *
   * 🔴 **アクション（`action`）は消さない。** 管理者は同じアクションのポリシーを続けて足すことが多い。
   */
  resetAfterSave: () => void;
}

/**
 * @param attributes 属性辞書（対象属性の選択肢と scope の出どころ）
 */
export function usePolicyDraft(attributes: readonly AttributeDefinitionDto[]): PolicyDraft {
  const [name, setName] = useState('');
  const [action, setAction] = useState<PolicyAction>('read');
  const [conditions, setConditions] = useState<ConditionEntry[]>([]);
  const [attributeKey, setAttributeKey] = useState('');
  const [conditionValue, setConditionValue] = useState('');

  const selected = attributes.find((a) => a.key === attributeKey);

  return {
    name,
    setName,
    action,
    setAction,
    conditions,
    attributeKey,
    selectAttributeKey: (key: string) => {
      setAttributeKey(key);
      setConditionValue('');
    },
    conditionValue,
    setConditionValue,
    selected,
    values: selected?.allowedValues ?? [],
    addCondition: () => {
      if (!selected || conditionValue === '') return;
      setConditions((prev) => [
        ...prev,
        { scope: selected.scope, key: selected.key, value: conditionValue },
      ]);
      setConditionValue('');
    },
    removeCondition: (index: number) => setConditions((prev) => prev.filter((_, j) => j !== index)),
    canSubmit: name.trim().length > 0,
    body: () => ({ name: name.trim(), action, ...buildConditions(conditions) }),
    resetAfterSave: () => {
      setName('');
      setConditions([]);
    },
  };
}
