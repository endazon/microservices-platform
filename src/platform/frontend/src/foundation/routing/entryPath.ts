/**
 * アプリのルート直下（`/`）の遷移先、および認証導線が戻り先を決められなかったときの既定（IADR-0124 決定 6）。
 *
 * 計画の画面一覧（SC-01〜21）に home に相当する画面は無く、SC-01 が「本システムの主入口」と
 * 定義されている（05_screens §SC-01）。`/` の存在はアプリホスト（platform）の責務であり、
 * 可変ユニットを外しても消えてはならないため platform 側に置く。
 * 値は**パス文字列**であってユニットへの参照ではない（IADR-0056 決定 3 に抵触しない）。
 * 実在することは `router.test.ts` が実行時に固定する。
 *
 * 依存ゼロの単独モジュールにしてあるのは、`shell.tsx`（ルート定義）と
 * `auth/CallbackPage.tsx`（OIDC 復帰）の**両方**が要るためである。どちらかに置くと
 * `shell.tsx` → `CallbackPage` → `shell.tsx` の循環 import になる。
 */
export const ENTRY_ROUTE_PATH = '/ask';
