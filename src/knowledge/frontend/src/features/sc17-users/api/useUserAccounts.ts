import { useQueryClient } from '@tanstack/react-query';
import {
  getBffUserAdminListAssignableRolesQueryKey,
  getBffUserAdminListUsersQueryKey,
  useBffUserAdminDisableUser,
  useBffUserAdminEnableUser,
  useBffUserAdminListAssignableRoles,
  useBffUserAdminListUsers,
  useBffUserAdminReplaceUserAttributes,
  useBffUserAdminReplaceUserRoles,
} from '@foundation/api/generated/user-admin/user-admin';
import {
  getBffAuthzListAttributesQueryKey,
  useBffAuthzListAttributes,
} from '@foundation/api/generated/authorization/authorization';
import { okArray } from '@foundation/api/orvalSelect';
import type {
  AttributeDefinitionDto,
  PlatformUserDto,
} from '@foundation/api/generated/bff.schemas';

// SC-17, UC-05, FR-05, FR-09: 利用者アカウント管理の読み書き（/bff/admin/users）。
// サーバー状態は TanStack Query に一元化する（ADR-0031）。
//
// - **すべて orval 生成フックで呼ぶ**（手書きの HTTP クライアントは禁止）。
// - 変更操作の成功後は `invalidateQueries` だけを行う（手書きの再取得を持たない。IADR-0127 決定 5）。
// - 非 2xx は `apiRequest` が投げる。**400 の理由（RFC7807）は `ApiError.details` に載り続ける**ので、
//   画面はそこから拒否理由（辞書外の値・必須欠落・定義外ロール）を出せる。

// キャッシュキーは**この module の外へ出さない**（未使用 export の床を押し上げるだけである）。
const usersKey = getBffUserAdminListUsersQueryKey();
const assignableRolesKey = getBffUserAdminListAssignableRolesQueryKey();
const abacAttributesKey = getBffAuthzListAttributesQueryKey();

/** 利用者一覧（SC-17 主要素 1）。 */
export function useUserAccounts() {
  return useBffUserAdminListUsers<PlatformUserDto[], unknown>({
    // 既定値は残す（IADR-0132 決定 3）。**`?? []` ではなく `okArray`** ——
    // 空ボディで `{}` が届くと `{} ?? []` は発火せず、`{}.map` でクラッシュする。
    query: { queryKey: usersKey, select: okArray },
  });
}

/**
 * 割当可能なロール（SC-17 入力規則「定義済みロールのみ」）。
 *
 * 🔴 **画面へロールの値集合を焼き込まない。** 計画は 4 種（利用者／管理者／運用者／
 * システム管理者）を挙げるが、認可基盤の側はまだそこまで分かれていない。焼き込むと
 * 「計画には在るが選ぶと必ず失敗する選択肢」を描くことになる。**在るものだけを出す。**
 */
export function useAssignableRoles() {
  return useBffUserAdminListAssignableRoles<string[], unknown>({
    query: { queryKey: assignableRolesKey, select: okArray },
  });
}

/**
 * ABAC 属性の辞書（SC-17 入力規則「定義済みの値のみ」）。
 *
 * 値域の正は辞書側（SC-09 の属性体系・タグ辞書）であり、画面はそれを引くだけである。
 */
export function useAbacAttributeDictionary() {
  return useBffAuthzListAttributes<AttributeDefinitionDto[], unknown>({
    query: { queryKey: abacAttributesKey, select: okArray },
  });
}

/**
 * 割当の保存と、アカウントの無効化・再有効化。
 *
 * 無効化は**全セッション失効を伴う**（後段が 1 つの操作として実行する）。画面側も一覧を
 * 無効化して状態を引き直す。
 */
export function useUserAccountActions() {
  const queryClient = useQueryClient();
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: usersKey });
  const onSuccess = { mutation: { onSuccess: invalidate } };

  const replaceRoles = useBffUserAdminReplaceUserRoles<unknown>(onSuccess);
  const replaceAttributes = useBffUserAdminReplaceUserAttributes<unknown>(onSuccess);
  const disable = useBffUserAdminDisableUser<unknown>(onSuccess);
  const enable = useBffUserAdminEnableUser<unknown>(onSuccess);

  return { replaceRoles, replaceAttributes, disable, enable };
}
