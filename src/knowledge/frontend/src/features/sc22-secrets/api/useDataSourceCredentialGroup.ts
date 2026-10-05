import { useQueryClient } from '@tanstack/react-query';
import {
  getBffSecretItemGroupListQueryKey,
  useBffSecretItemGroupList,
  useBffSecretItemGroupUpdate,
} from '@foundation/api/generated/secret-items/secret-items';
import { okData } from '@foundation/api/orvalSelect';
import type { SecretItemGroupDto } from '@foundation/api/generated/bff.schemas';

// SC-22, SC-06, 計画 ADR-0126 決定 1・3・4, IADR-0501 (#458 段 S3): 群「データソースの資格情報」の一覧と、1 プロパティずつの書き込み。
// サーバー状態は TanStack Query に一元化する（ADR-0031）。**すべて orval 生成フックで呼ぶ。**
// 🔴 **値を読み出す口は無い**（契約にも存在しない）。

/** 群の名前（`deploy/bootstrap/sc22-secret-items.json` の `groups[].group`）。 */
export const DATASOURCE_CREDENTIALS_GROUP = 'datasource-credentials';

const groupKey = getBffSecretItemGroupListQueryKey(DATASOURCE_CREDENTIALS_GROUP);

/**
 * 本文の形を確かめて正規化する。🔴 **`writable` は `true` のときだけ真**（無い・壊れた本文で更新の操作を出さない）。
 * 成員が配列でなければ空へ倒す（空ボディの `{}` で落ちない。IADR-0135 決定 7 と同じ理由）。
 */
function normalize(value: unknown): SecretItemGroupDto {
  const body = (value ?? {}) as Partial<SecretItemGroupDto>;
  return {
    group: typeof body.group === 'string' ? body.group : DATASOURCE_CREDENTIALS_GROUP,
    writable: body.writable === true,
    members: Array.isArray(body.members) ? body.members : [],
  };
}

/** 群の成員の一覧（運用者・システム管理者）。 */
export function useDataSourceCredentialGroup() {
  return useBffSecretItemGroupList<SecretItemGroupDto, unknown>(DATASOURCE_CREDENTIALS_GROUP, {
    query: { queryKey: groupKey, select: (res) => normalize(okData(res)) },
  });
}

/** 成員 1 件の 1 プロパティの書き込み（管理者だけ）。成功したら一覧を引き直す。 */
export function useDataSourceCredentialUpdate() {
  const queryClient = useQueryClient();
  return useBffSecretItemGroupUpdate<unknown>({
    mutation: { onSuccess: () => void queryClient.invalidateQueries({ queryKey: groupKey }) },
  });
}
