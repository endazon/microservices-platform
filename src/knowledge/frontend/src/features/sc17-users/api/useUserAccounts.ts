import { useQueries, useQueryClient } from '@tanstack/react-query';
import {
  getBffUserAdminGetUserDepartmentQueryKey,
  getBffUserAdminListAssignableRolesQueryKey,
  getBffUserAdminListUsersQueryKey,
  useBffUserAdminDisableUser,
  useBffUserAdminEnableUser,
  useBffUserAdminGetUserDepartment,
  useBffUserAdminListAssignableRoles,
  useBffUserAdminListUsers,
  useBffUserAdminReplaceUserAttributes,
  useBffUserAdminReplaceUserDepartment,
  useBffUserAdminReplaceUserRoles,
} from '@foundation/api/generated/user-admin/user-admin';
import {
  getBffAuthzListAttributesQueryKey,
  useBffAuthzListAttributes,
} from '@foundation/api/generated/authorization/authorization';
import {
  getBffPrivateNoteQuotaGetQueryKey,
  getBffPrivateNoteQuotaLookupQueryKey,
  getBffPrivateNoteQuotaLookupQueryOptions,
  useBffPrivateNoteQuotaGet,
  useBffPrivateNoteQuotaSet,
} from '@foundation/api/generated/private-notes/private-notes';
import { okArray, okData } from '@foundation/api/orvalSelect';
import type {
  AttributeDefinitionDto,
  PlatformUserDto,
  PrivateNoteOwnerUsageDto,
  PrivateNoteQuotaLookupResponse,
  PrivateNoteUsageDto,
  UserDepartmentDto,
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
 * 利用者の部門（#1610・計画 ADR-0116 決定 1）: 部門グループの所属・属性・部門欄の選択肢。
 *
 * 🔴 **部門欄の選択肢は realm の部門グループのコード**（`choices`）であり、画面へ焼き込まない。
 * 部門の正本は部門グループであり、属性（`departmentAttribute`）は部門の同期が追いつくまで違い得る。
 * 編集を開いた利用者だけを引く（`userId` が null の間は引かない）。
 */
export function useUserDepartment(userId: string | null) {
  const id = userId ?? '';
  return useBffUserAdminGetUserDepartment<UserDepartmentDto, unknown>(id, {
    query: {
      queryKey: getBffUserAdminGetUserDepartmentQueryKey(id),
      select: okData,
      enabled: id.length > 0,
    },
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
  // #1610: 部門の変更（部門グループの所属の変更）。成功しても失敗しても、その人の部門を引き直す
  // （途中の失敗で補償した後の所属も画面に出す）。一覧（属性）も引き直す。
  const replaceDepartment = useBffUserAdminReplaceUserDepartment<unknown>({
    mutation: {
      onSettled: (_data, _error, variables) => {
        invalidate();
        void queryClient.invalidateQueries({
          queryKey: getBffUserAdminGetUserDepartmentQueryKey(variables.userId),
        });
      },
    },
  });
  const disable = useBffUserAdminDisableUser<unknown>(onSuccess);
  const enable = useBffUserAdminEnableUser<unknown>(onSuccess);

  return { replaceRoles, replaceAttributes, replaceDepartment, disable, enable };
}

/**
 * 個人資料の保存容量（FR-19, NFR-27, SC-17。#2330）: 使用量・上限・割合の**数だけ**。
 *
 * 🔴 **題名・件数・中身は引かない**（応答にも無い）。SC-19 の「管理者が他の利用者の個人資料を閲覧する画面を描かない」。
 * 鍵は利用者名（`preferred_username`。後段の個人資料の所有者と同じ値）。行ごとに 1 本引き、キャッシュを共有する。
 */
export function usePrivateNoteQuota(username: string) {
  return useBffPrivateNoteQuotaGet<PrivateNoteUsageDto, unknown>(username, {
    query: { queryKey: getBffPrivateNoteQuotaGetQueryKey(username), select: okData },
  });
}

/** 一括照会の 1 本あたりの上限（後段の検証と同じ）。これを超える行数は分けて引く。 */
const PRIVATE_NOTE_QUOTA_LOOKUP_MAX = 1000;

// 一括照会のキャッシュの接頭辞（上限の変更の後に、まとめて引き直すため）。
const quotaLookupKeyPrefix = ['POST', '/bff/private-notes/quotas/lookup'] as const;

/**
 * FR-19, NFR-27, SC-17 (#2348): 一覧の利用者（絞り込み前の全行）の容量を**まとめて**引く（行ごとの照会〔N+1〕をしない）。
 * 1,000 人までは 1 本、それを超えたら 1,000 人ずつに分ける。読めた分だけを利用者名で引ける形に畳む。
 * 一部の束が読めなくても、他の束の値は出す（読めない行は「—」）。
 */
export function usePrivateNoteQuotaLookup(
  usernames: readonly string[],
): ReadonlyMap<string, PrivateNoteOwnerUsageDto> {
  const distinct = [...new Set(usernames)];
  const chunks: string[][] = [];
  for (let i = 0; i < distinct.length; i += PRIVATE_NOTE_QUOTA_LOOKUP_MAX) {
    chunks.push(distinct.slice(i, i + PRIVATE_NOTE_QUOTA_LOOKUP_MAX));
  }
  const results = useQueries({
    queries: chunks.map((ownerIds) =>
      getBffPrivateNoteQuotaLookupQueryOptions<PrivateNoteQuotaLookupResponse, unknown>(
        { ownerIds },
        { query: { queryKey: getBffPrivateNoteQuotaLookupQueryKey({ ownerIds }), select: okData } },
      ),
    ),
  });
  const byOwner = new Map<string, PrivateNoteOwnerUsageDto>();
  for (const r of results) {
    for (const item of r.data?.items ?? []) byOwner.set(item.ownerId, item);
  }
  return byOwner;
}

/** 上限の変更（0〜1 TB。範囲外は後段が 400）。成功後はその人の容量を引き直す。 */
export function useSetPrivateNoteQuota(username: string) {
  const queryClient = useQueryClient();
  return useBffPrivateNoteQuotaSet<unknown>({
    mutation: {
      onSuccess: () => {
        void queryClient.invalidateQueries({
          queryKey: getBffPrivateNoteQuotaGetQueryKey(username),
        });
        // #2348: 一覧の欄は一括照会から描くので、それも引き直す。
        void queryClient.invalidateQueries({ queryKey: quotaLookupKeyPrefix });
      },
    },
  });
}
