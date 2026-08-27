// NFR, ADR-0032, IADR-0273, #439: 認証導線のトップレベル遷移（SPA 内遷移ではなくページ全体の移動）。
//
// ログイン・ログアウトは認可サーバ（Keycloak）との**ブラウザの往復**であり、SPA のルータでは
// 完結しない。遷移をこの 1 関数へ集約するのは、jsdom が `location.assign` を実装せず
// `window.location` の差し替えも許さないため —— テストはこのモジュールをモックする。
export function hardNavigate(url: string): void {
  window.location.assign(url);
}
