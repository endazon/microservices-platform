import { useQueryClient } from '@tanstack/react-query';
import {
  getBffSecretItemsListQueryKey,
  useBffSecretItemsList,
  useBffSecretItemsUpdate,
} from '@foundation/api/generated/secret-items/secret-items';
import { okArray } from '@foundation/api/orvalSelect';
import type { SecretItemStatusDto } from '@foundation/api/generated/bff.schemas';

// SC-22, FR-05, ADR-0095 決定 3, IADR-0433, IADR-0453: 秘密情報の項目の一覧と、1 プロパティずつの書き込み。
// サーバー状態は TanStack Query に一元化する（ADR-0031）。**すべて orval 生成フックで呼ぶ。**
//
// 🔴 **値を読み出す口は無い**（契約にも存在しない）。一覧が返すのは状態・版・時刻・最終更新者だけである。

const itemsKey = getBffSecretItemsListQueryKey();

/** 項目の一覧（SC-22 主要素 1・3）。 */
export function useSecretItems() {
  return useBffSecretItemsList<SecretItemStatusDto[], unknown>({
    // 既定値は残す（IADR-0132 決定 3）。空ボディで `{}` が届いても落ちないよう `okArray` を通す。
    query: { queryKey: itemsKey, select: okArray },
  });
}

/**
 * 1 プロパティの書き込み（SC-22 主要素 2）。
 *
 * 成功したら一覧を引き直す（版・最終更新日時・最終更新者が変わる）。手書きの再取得は持たない（IADR-0127 決定 5）。
 */
export function useSecretItemUpdate() {
  const queryClient = useQueryClient();
  return useBffSecretItemsUpdate<unknown>({
    mutation: { onSuccess: () => void queryClient.invalidateQueries({ queryKey: itemsKey }) },
  });
}
