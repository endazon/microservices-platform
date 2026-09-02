import { useMemo, useState } from 'react';
import type { AttributeDefinitionDto } from '@foundation/api/generated/bff.schemas';
import {
  assignableAttributes,
  buildAttributes,
  requiresAttributes,
  validateRegistration,
} from '../types/mcpClientVocabulary';
import type { AttributeEntry, ClientKind, RegistrationIssue } from '../types/mcpClientVocabulary';

// SC-12, UC-09, FR-16: クライアント登録フォームの**クライアント状態**
// （計画 13_frontend-stack §ディレクトリ構成 の `hooks/`。IADR-0309 決定 1）。
//
// **サーバー状態をここへ持ち込まない** —— 一覧の取得も登録の送信も `api/useMcpClients.ts` の
// TanStack Query が持つ（ADR-0031）。ここに在るのは「まだ送っていない下書き」だけである。
// **ミューテーションをこのフックへ入れない** —— 入れるとサーバー状態との境界が消え、
// 画面もサーバーも無しに遷移を試験できなくなる（本フックの存在理由がそれである）。
//
// **入力規則そのものは `types/mcpClientVocabulary.ts` の純関数が持つ**（`validateRegistration` 等）。
// ここが持つのは**遷移**である —— どの操作が何を消し、何を残すか。

/** 登録フォームの下書きと操作。 */
export interface McpClientRegistrationForm {
  clientId: string;
  setClientId: (value: string) => void;
  displayName: string;
  setDisplayName: (value: string) => void;
  kind: ClientKind;
  setKind: (value: ClientKind) => void;
  /** 属性割当に選べる辞書項目（利用者スコープかつ許可値を持つものだけ）。 */
  definitions: AttributeDefinitionDto[];
  attributeKey: string;
  /** 属性を選び直す。**値は消える** —— 別の属性の許可値をそのまま持ち越さない。 */
  selectAttributeKey: (key: string) => void;
  attributeValue: string;
  setAttributeValue: (value: string) => void;
  /** いま選んでいる属性の定義（許可値の一覧を引くため）。 */
  selectedDefinition: AttributeDefinitionDto | undefined;
  entries: AttributeEntry[];
  /** 選んでいる属性と値を積む。**キーか値が空なら何もしない／同じキーは後勝ちで 1 件だけ残る。** */
  addEntry: () => void;
  /** 種別が属性を要求するか（無人＝サービスアカウントのみ）。 */
  needsAttributes: boolean;
  issues: RegistrationIssue[];
  /** 入力規則を検査し結果を保持する。**送ってよいときだけ true** を返す。 */
  validate: () => boolean;
  /** 契約の形に畳んだ登録本文。有人には属性を含めない（送る値が無いのが正しい）。 */
  body: () => {
    clientId: string;
    displayName: string;
    kind: ClientKind;
    attributes?: Record<string, string>;
  };
  /**
   * 登録成功後の後始末。
   *
   * 🔴 **種別（`kind`）は消さない。** 管理者は同じ種別のクライアントを続けて登録することが多く、
   * 既定値へ戻すと毎回選び直させることになる。**属性の下書きは消す**（別のクライアントのものである）。
   */
  resetAfterRegister: () => void;
}

export function useMcpClientRegistrationForm(
  dictionary: readonly AttributeDefinitionDto[],
): McpClientRegistrationForm {
  const [clientId, setClientId] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [kind, setKind] = useState<ClientKind>('interactive');
  const [attributeKey, setAttributeKey] = useState('');
  const [attributeValue, setAttributeValue] = useState('');
  const [entries, setEntries] = useState<AttributeEntry[]>([]);
  const [issues, setIssues] = useState<RegistrationIssue[]>([]);

  const definitions = useMemo(() => assignableAttributes(dictionary), [dictionary]);
  const selectedDefinition = definitions.find((d) => d.key === attributeKey);

  const selectAttributeKey = (key: string) => {
    setAttributeKey(key);
    setAttributeValue('');
  };

  const addEntry = () => {
    if (!attributeKey || !attributeValue) return;
    setEntries((prev) => [
      ...prev.filter((e) => e.key !== attributeKey),
      { key: attributeKey, value: attributeValue },
    ]);
    setAttributeValue('');
  };

  const validate = () => {
    const found = validateRegistration({ clientId, displayName, kind, attributes: entries });
    setIssues(found);
    return found.length === 0;
  };

  return {
    clientId,
    setClientId,
    displayName,
    setDisplayName,
    kind,
    setKind,
    definitions,
    attributeKey,
    selectAttributeKey,
    attributeValue,
    setAttributeValue,
    selectedDefinition,
    entries,
    addEntry,
    needsAttributes: requiresAttributes(kind),
    issues,
    validate,
    body: () => ({
      clientId: clientId.trim(),
      displayName: displayName.trim(),
      kind,
      ...(requiresAttributes(kind) ? { attributes: buildAttributes(entries) } : {}),
    }),
    resetAfterRegister: () => {
      setClientId('');
      setDisplayName('');
      setEntries([]);
    },
  };
}
