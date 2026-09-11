// Issue #126: 実行時 config（dev 既定）。本番はコンテナ起動時に config.js.template から envsubst で生成する。
// ビルド成果物を環境非依存に保ち、接続先（BFF・Keycloak）をデプロイ時に切り替えられるようにする（疎結合）。
window.__APP_CONFIG__ = {
  // BFF の基点。dev は Vite proxy 経由の相対パス（/bff）。本番はエッジの BFF URL。
  bffBaseUrl: '/bff',
  // Keycloak の issuer。**SPA は OIDC を実施しない**（BFF セッション方式・ADR-0032）。
  // この値はアカウント設定画面への導線（Keycloak のアカウントコンソール）にだけ使う。
  oidc: {
    authority: 'http://localhost:8080/realms/platform',
  },
};
