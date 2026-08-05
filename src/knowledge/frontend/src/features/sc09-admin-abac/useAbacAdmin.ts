import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@foundation/api/apiClient';
import type { ConditionMap } from './abacVocabulary';

// SC-09, UC-05, FR-09: 属性辞書・ポリシーの照会と編集（サーバー状態は TanStack Query。ADR-0031）。
//
// BFF（/bff/admin/authz/*）は AuthorizationService へ透過中継し、保存前検証の 400・
// 参照中削除の 409 をそのまま返す（IADR-0040 / IADR-0006）。画面はその詳細を検証結果として出す。
//
// `/bff/admin/authz` は docs/api/openapi.yaml に無く orval 生成物が存在しないため、
// `apiFetch` ＋ 本ファイルの手書き型で呼ぶ（#506 の射程）。手書き HTTP クライアントではない
// ——出口は foundation/api の 1 箇所に収束している（IADR-0121 決定 3）。
//
// IADR-0127 決定 5 と同じ作法: 変更操作の成功後は invalidateQueries だけを行う（手書きの再取得を持たない）。

/** 属性辞書エントリ（`AttributeDefinitionDto`）。 */
export interface AttributeDefinition {
  id: string;
  key: string;
  label: string;
  allowedValues: string[];
  required: boolean;
  /** 契約の 2 値（document / user）。未知の値も受け取る。 */
  scope: string;
}

/** ABAC ポリシー（`AbacPolicyDto`）。 */
export interface AbacPolicy {
  id: string;
  name: string;
  /** 契約の 3 値（read / analyze / manage）。未知の値も受け取る。 */
  action: string;
  userConditions: ConditionMap;
  documentConditions: ConditionMap;
  isActive: boolean;
}

export interface NewAttribute {
  key: string;
  label: string;
  allowedValues: string[];
  required: boolean;
  scope: string;
}

export interface NewPolicy {
  name: string;
  action: string;
  userConditions: ConditionMap;
  documentConditions: ConditionMap;
}

const ATTRIBUTES_PATH = '/admin/authz/attributes';
const POLICIES_PATH = '/admin/authz/policies';

export const abacAttributesKey = ['bff', 'admin', 'authz', 'attributes'] as const;
export const abacPoliciesKey = ['bff', 'admin', 'authz', 'policies'] as const;

/** 属性辞書の一覧（UC-05 基本フロー「属性を定義する」）。 */
export function useAbacAttributes() {
  return useQuery({
    queryKey: abacAttributesKey,
    queryFn: async () => (await apiFetch<AttributeDefinition[]>(ATTRIBUTES_PATH)) ?? [],
  });
}

/** ポリシーの一覧（UC-05 基本フロー「ポリシーを定義する」）。 */
export function useAbacPolicies() {
  return useQuery({
    queryKey: abacPoliciesKey,
    queryFn: async () => (await apiFetch<AbacPolicy[]>(POLICIES_PATH)) ?? [],
  });
}

/**
 * 属性辞書の編集（追加・削除）。
 *
 * 削除は**参照中なら 409** で拒否される（IADR-0006）。呼び出し側が理由を表示する。
 */
export function useAttributeActions() {
  const queryClient = useQueryClient();
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: abacAttributesKey });

  const create = useMutation({
    mutationFn: (attribute: NewAttribute) =>
      apiFetch(ATTRIBUTES_PATH, { method: 'POST', json: attribute }),
    onSuccess: invalidate,
  });

  const remove = useMutation({
    mutationFn: (id: string) => apiFetch(`${ATTRIBUTES_PATH}/${id}`, { method: 'DELETE' }),
    onSuccess: invalidate,
  });

  return { create, remove };
}

/**
 * ポリシーの編集（追加・有効／無効切替・削除）。
 *
 * 追加は**保存前に矛盾検証**され、矛盾があれば 400（`ValidationProblem`）で拒否される。
 * 呼び出し側が検証結果として詳細を表示する（計画 §SC-09 §アクション）。
 */
export function usePolicyActions() {
  const queryClient = useQueryClient();
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: abacPoliciesKey });

  const create = useMutation({
    mutationFn: (policy: NewPolicy) => apiFetch(POLICIES_PATH, { method: 'POST', json: policy }),
    onSuccess: invalidate,
  });

  const setActive = useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) =>
      apiFetch(`${POLICIES_PATH}/${id}/active`, { method: 'PATCH', json: { isActive } }),
    onSuccess: invalidate,
  });

  const remove = useMutation({
    mutationFn: (id: string) => apiFetch(`${POLICIES_PATH}/${id}`, { method: 'DELETE' }),
    onSuccess: invalidate,
  });

  return { create, setActive, remove };
}
