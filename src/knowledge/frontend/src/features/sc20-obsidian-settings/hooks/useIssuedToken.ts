import { useState } from 'react';
import type { SyncTokenIssuedResponse } from '@foundation/api/generated/bff.schemas';

// SC-20, UC-11, FR-20: 平文トークンの一時状態（計画 13_frontend-stack §ディレクトリ構成 の `hooks/`）。
//
// 🔴 **平文のトークンは、発行・再発行の応答にしか載らない。** 画面はそれを**その場の状態**として
// 持ち、次の操作を始めた時点で捨てる（05_screens §SC-20「保存もコピー履歴も残さない」）。
//
// 🔴 **だからこの状態を `stores/` のクライアントストアへ置いてはならない。** ストアは画面をまたいで
// 生き延びるための道具であり、平文トークンに対しては**仕様違反**になる。URL にも載せない
// （履歴・共有・再読込のいずれでも漏れる。ルートのコメントを参照）。
// 保持先を「React のローカル状態ただ 1 つ」に閉じるために、この hook を独立させている。

export interface IssuedToken {
  /** 直近の発行・再発行の応答。表示していないときは `null`。 */
  issued: SyncTokenIssuedResponse | null;
  /** 発行・再発行が成功したときだけ平文を受け取る。 */
  show: (response: SyncTokenIssuedResponse) => void;
  /** 表示を捨てる（新しい操作を始めるとき・画面から離れるとき）。 */
  clear: () => void;
}

export function useIssuedToken(): IssuedToken {
  const [issued, setIssued] = useState<SyncTokenIssuedResponse | null>(null);
  return {
    issued,
    show: (response: SyncTokenIssuedResponse) => setIssued(response),
    clear: () => setIssued(null),
  };
}
