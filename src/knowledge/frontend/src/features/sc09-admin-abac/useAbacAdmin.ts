import { useQueryClient } from '@tanstack/react-query';
import {
  getBffAuthzListAttributesQueryKey,
  getBffAuthzListPoliciesQueryKey,
  useBffAuthzCreateAttribute,
  useBffAuthzCreatePolicy,
  useBffAuthzValidatePolicy,
  useBffAuthzDeleteAttribute,
  useBffAuthzDeletePolicy,
  useBffAuthzListAttributes,
  useBffAuthzListPolicies,
  useBffAuthzSetPolicyActive,
} from '@foundation/api/generated/authorization/authorization';
import { okArray } from '@foundation/api/orvalSelect';
import type { AbacPolicyDto, AttributeDefinitionDto } from '@foundation/api/generated/bff.schemas';

// SC-09, UC-05, FR-09: 属性辞書・ポリシーの照会と編集（サーバー状態は TanStack Query。ADR-0031）。
//
// BFF（/bff/admin/authz/*）は AuthorizationService へ透過中継し、保存前検証の 400・
// 参照中削除の 409 をそのまま返す（IADR-0040 / IADR-0006）。画面はその詳細を検証結果として出す。
// **その詳細は `ApiError.details` に載り続ける**——非 2xx は生成コードでも `apiRequest` が投げるため、
// 400 / 409 の Problem 本文の抽出（apiClient.parseProblemDetails）は載せ替えの影響を受けない。
//
// IADR-0135 決定 1（#519）: 7 本とも **orval 生成フック**で呼ぶ（`/bff/admin/authz` 群は #506 で契約が揃った）。
// IADR-0127 決定 5 と同じ作法: 変更操作の成功後は invalidateQueries だけを行う（手書きの再取得を持たない）。

export const abacAttributesKey = getBffAuthzListAttributesQueryKey();
export const abacPoliciesKey = getBffAuthzListPoliciesQueryKey();

/** 属性辞書の一覧（UC-05 基本フロー「属性を定義する」）。 */
export function useAbacAttributes() {
  return useBffAuthzListAttributes<AttributeDefinitionDto[], unknown>({
    // 既定値は残す（IADR-0132 決定 3）。契約上は必須でも、実行時に本文を検証する層は無い。
    // **`?? []` ではなく `okArray`**——`bffFetch` は本文が空なら `{}` を返すため
    // `{} ?? []` は発火せず、`{}` が `attributes.map` へ届いてクラッシュしていた
    // （IADR-0135 決定 7［2026-08-06 追記］）。
    query: { queryKey: abacAttributesKey, select: okArray },
  });
}

/** ポリシーの一覧（UC-05 基本フロー「ポリシーを定義する」）。 */
export function useAbacPolicies() {
  return useBffAuthzListPolicies<AbacPolicyDto[], unknown>({
    query: { queryKey: abacPoliciesKey, select: okArray },
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
  const onSuccess = { mutation: { onSuccess: invalidate } };

  const create = useBffAuthzCreateAttribute<unknown>(onSuccess);
  const remove = useBffAuthzDeleteAttribute<unknown>(onSuccess);

  return { create, remove };
}

/**
 * ポリシーの編集（追加・有効／無効切替・削除）＋ **dry-run 検証**。
 *
 * 追加は**保存前に矛盾検証**され、矛盾があれば 400（`ValidationProblem`）で拒否される。
 * 呼び出し側が検証結果として詳細を表示する（計画 §SC-09 §アクション）。
 *
 * **［#535］`validate` は保存せず同じ検証だけを走らせる**（裁定 Q23）。
 * **キャッシュを無効化しない**——何も変えていないためである
 * （`create` / `setActive` / `remove` は一覧を書き換えるので無効化する）。
 */
export function usePolicyActions() {
  const queryClient = useQueryClient();
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: abacPoliciesKey });
  const onSuccess = { mutation: { onSuccess: invalidate } };

  const create = useBffAuthzCreatePolicy<unknown>(onSuccess);
  const setActive = useBffAuthzSetPolicyActive<unknown>(onSuccess);
  const remove = useBffAuthzDeletePolicy<unknown>(onSuccess);
  const validate = useBffAuthzValidatePolicy<unknown>();

  return { create, setActive, remove, validate };
}
