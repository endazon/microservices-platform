import { useCallback, useState } from 'react';

// SC-12, UC-09, FR-16（#1845）: 無人の MCP クライアントの client secret の一時状態（計画 05_screens §SC-12 の 2026-10-09 補完）。
//
// 🔴 **平文の secret は、無人の登録（201）と再発行（200）の応答にしか載らない。** 画面はそれを**その場の状態**として持ち、
// 閉じたとき・次の操作を始めたときに捨てる（「再表示はできない」。プラットフォームは値を保存しない）。
//
// 🔴 **だからこの状態を `stores/` のクライアントストアや URL に置いてはならない**（画面をまたいで生き延び、履歴・共有・再読込で漏れる）。
// 保持先を「React のローカル状態ただ 1 つ」に閉じるために、この hook を独立させている（SC-20 の `useIssuedToken` と同じ作法）。

interface IssuedClientSecret {
  /** どのクライアントの secret か。 */
  clientId: string;
  /** 平文の client secret（この表示の間だけ持つ）。 */
  secret: string;
  /** 登録で発行したか、再発行したか（文言を出し分ける）。 */
  origin: 'issued' | 'reissued';
}

interface IssuedClientSecretState {
  issued: IssuedClientSecret | null;
  show: (value: IssuedClientSecret) => void;
  clear: () => void;
}

export function useIssuedClientSecret(): IssuedClientSecretState {
  const [issued, setIssued] = useState<IssuedClientSecret | null>(null);
  // 一覧の列定義（`useMemo`）から呼ぶので参照を固定する（毎描画で列定義を作り直さない）。
  const show = useCallback((value: IssuedClientSecret) => setIssued(value), []);
  const clear = useCallback(() => setIssued(null), []);
  return { issued, show, clear };
}
