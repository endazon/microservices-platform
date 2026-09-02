import { useCallback, useEffect, useState } from 'react';
import type {
  AttributeDefinitionDto,
  PlatformUserDto,
} from '@foundation/api/generated/bff.schemas';
import { validateAssignment } from '../types/userAccountVocabulary';
import type { AssignmentIssue } from '../types/userAccountVocabulary';

// SC-17, UC-05, FR-05/FR-09: 権限編集の**クライアント状態**
// （計画 13_frontend-stack §ディレクトリ構成 の `hooks/`。IADR-0309 決定 1）。
//
// **サーバー状態をここへ持ち込まない** —— 一覧の取得も反映も `api/useUserAccounts.ts` の
// TanStack Query が持つ（ADR-0031）。ここに在るのは「まだ送っていない下書き」だけである。
// **ミューテーションをこのフックへ入れない** —— 入れると画面もサーバーも無しに遷移を試験できなくなる。
//
// 入力規則そのものは `types/userAccountVocabulary.ts` の `validateAssignment`（純関数）が持つ。
// ここが持つのは**遷移**である。

/** 権限編集ダイアログの下書きと操作。 */
export interface UserPermissionEditor {
  /** 編集対象の利用者。閉じているときは null。 */
  editing: PlatformUserDto | null;
  /**
   * 行の「編集」から開く。
   *
   * 🔴 **参照が安定している**（`useCallback`）。一覧の列定義（`useMemo`）から呼ぶので、
   * 描画のたびに別関数になると列定義が毎回作り直される。
   */
  open: (userId: string) => void;
  close: () => void;
  draftRoles: string[];
  /** ロールを付け外しする（チェックボックスのトグル）。 */
  toggleRole: (role: string) => void;
  draftAttributes: Record<string, string>;
  /**
   * 属性を置く。
   *
   * 🔴 **空文字を渡すとキーごと落ちる。** 反映は差し替え（置換）なので、
   * 「送らない」ことが「外す」ことである —— 空文字のまま送ると、値として空文字が入る。
   */
  setAttribute: (key: string, value: string) => void;
  issues: AssignmentIssue[];
  /** 入力規則を検査し結果を保持する。**送ってよいときだけ true** を返す。 */
  validate: (definitions: readonly AttributeDefinitionDto[]) => boolean;
}

/**
 * @param users 取得済みの利用者一覧（編集対象をここから引く）
 */
export function useUserPermissionEditor(users: readonly PlatformUserDto[]): UserPermissionEditor {
  const [editingId, setEditingId] = useState<string | null>(null);
  const [draftRoles, setDraftRoles] = useState<string[]>([]);
  const [draftAttributes, setDraftAttributes] = useState<Record<string, string>>({});
  const [issues, setIssues] = useState<AssignmentIssue[]>([]);

  const editing = users.find((u) => u.id === editingId) ?? null;

  // 参照を固定する（`useState` の setter だけを呼ぶので、これ自体が安定している）。
  const open = useCallback((userId: string) => setEditingId(userId), []);

  // 一覧が入れ替わったら編集中の下書きを引き直す（他の管理者の変更を握り潰さない）。
  useEffect(() => {
    if (!editing) return;
    setDraftRoles([...editing.roles]);
    setDraftAttributes({ ...editing.attributes });
    // 🔴 **対象が変わったときだけ引き直す。** `editing` を依存に入れると、一覧が再取得された
    // だけで入力途中の下書きが毎回潰れる。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [editingId]);

  return {
    editing,
    open,
    close: () => setEditingId(null),
    draftRoles,
    toggleRole: (role: string) =>
      setDraftRoles((prev) =>
        prev.includes(role) ? prev.filter((r) => r !== role) : [...prev, role],
      ),
    draftAttributes,
    setAttribute: (key: string, value: string) =>
      setDraftAttributes((prev) => {
        // 任意属性を空へ戻したらキーごと落とす（差し替えなので、送らなければ外れる）。
        if (value === '') {
          const next = { ...prev };
          delete next[key];
          return next;
        }
        return { ...prev, [key]: value };
      }),
    issues,
    validate: (definitions: readonly AttributeDefinitionDto[]) => {
      const found = validateAssignment({
        roles: draftRoles,
        attributes: draftAttributes,
        definitions,
      });
      setIssues(found);
      return found.length === 0;
    },
  };
}
