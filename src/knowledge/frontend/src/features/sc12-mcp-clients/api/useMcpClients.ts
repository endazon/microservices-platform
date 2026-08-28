import { useQueryClient } from '@tanstack/react-query';
import {
  getBffMcpListClientsQueryKey,
  getBffMcpListToolsQueryKey,
  useBffMcpDisableClient,
  useBffMcpEnableClient,
  useBffMcpListClients,
  useBffMcpListTools,
  useBffMcpRegisterClient,
  useBffMcpReplaceClientAttributes,
} from '@foundation/api/generated/mcp-clients/mcp-clients';
import {
  getBffAuthzListAttributesQueryKey,
  useBffAuthzListAttributes,
} from '@foundation/api/generated/authorization/authorization';
import { okArray, okData } from '@foundation/api/orvalSelect';
import type {
  AttributeDefinitionDto,
  EffectiveToolsView,
  McpClientView,
} from '@foundation/api/generated/bff.schemas';

// SC-12, UC-09, FR-16: MCP クライアント登録管理の読み書き（/bff/admin/mcp-clients）。
// サーバー状態は TanStack Query に一元化する（ADR-0031）。
//
// - **すべて orval 生成フックで呼ぶ**（手書きの HTTP クライアントは禁止）。
// - 変更操作の成功後は `invalidateQueries` だけを行う（手書きの再取得を持たない。IADR-0127 決定 5）。
// - 非 2xx は `apiRequest` が投げる。**400 の理由（RFC7807）は `ApiError.details` に載り続ける**ので、
//   画面はそこから拒否理由（無人アカウントへの個人資料属性割当の禁止等）を出せる。

// キャッシュキーは**この module の外へ出さない**。外から触る用が無いのに export すると
// 未使用 export の床（check-knip）を押し上げるだけである。
const mcpClientsKey = getBffMcpListClientsQueryKey();
const mcpToolsKey = getBffMcpListToolsQueryKey();
const abacAttributesKey = getBffAuthzListAttributesQueryKey();

/** 登録クライアントの一覧（SC-12 主要素 1）。 */
export function useMcpClients() {
  return useBffMcpListClients<McpClientView[], unknown>({
    // 既定値は残す（IADR-0132 決定 3）。**`?? []` ではなく `okArray`** ——
    // 空ボディで `{}` が届くと `{} ?? []` は発火せず、`{}.map` でクラッシュする。
    query: { queryKey: mcpClientsKey, select: okArray },
  });
}

/**
 * 実効ツール一覧と構成ドリフト（SC-12 主要素 4 / ADR-0024 §5）。
 *
 * 🔴 **読み取りしかない。** 公開ツールを変更する mutation を本 feature へ置かない ——
 * 公開範囲の変更は Git 経由の公開構成変更で行う（許可リスト方式・GitOps）。
 */
export function useEffectiveTools() {
  return useBffMcpListTools<EffectiveToolsView, unknown>({
    query: { queryKey: mcpToolsKey, select: okData },
  });
}

/**
 * ABAC 属性の辞書（SC-12 入力規則「定義済み機密区分・タグのみ」）。
 *
 * 🔴 **画面へ値集合を焼き込まない。** 焼き込むと辞書を増やしても選べず、逆に辞書から
 * 消えた値を選べてしまう。値域の正は辞書側であり、画面はそれを引くだけである。
 */
export function useAbacAttributeDictionary() {
  return useBffAuthzListAttributes<AttributeDefinitionDto[], unknown>({
    query: { queryKey: abacAttributesKey, select: okArray },
  });
}

/**
 * クライアントの登録・無効化・再有効化・属性割当。
 *
 * 無効化は**次の呼び出しから即座に**効く（後段がキャッシュを挟まない）。画面側も
 * 一覧を無効化して即座に状態を引き直す。
 */
export function useMcpClientActions() {
  const queryClient = useQueryClient();
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: mcpClientsKey });
  const onSuccess = { mutation: { onSuccess: invalidate } };

  const register = useBffMcpRegisterClient<unknown>(onSuccess);
  const disable = useBffMcpDisableClient<unknown>(onSuccess);
  const enable = useBffMcpEnableClient<unknown>(onSuccess);
  const replaceAttributes = useBffMcpReplaceClientAttributes<unknown>(onSuccess);

  return { register, disable, enable, replaceAttributes };
}
