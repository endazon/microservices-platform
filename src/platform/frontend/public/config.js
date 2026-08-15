// Issue #126: 実行時 config（dev 既定）。本番はコンテナ起動時に config.js.template から envsubst で生成する。
// ビルド成果物を環境非依存に保ち、接続先（BFF・Keycloak）をデプロイ時に切り替えられるようにする（疎結合）。
window.__APP_CONFIG__ = {
  // BFF の基点。dev は Vite proxy 経由の相対パス（/bff）。本番はエッジの BFF URL。
  bffBaseUrl: '/bff',
  // Keycloak OIDC（SPA public client + Authorization Code + PKCE）。
  oidc: {
    authority: 'http://localhost:8080/realms/platform',
    clientId: 'platform-spa',
  },
};
