// Issue #126: 実行時 config の読み込み。ビルド成果物を環境非依存に保ち、接続先（BFF・Keycloak）を
// デプロイ時に切り替えられるようにする（バックエンドとフロントの疎結合。BFF/OpenAPI が境界）。
// 優先順: window.__APP_CONFIG__（public/config.js・本番は envsubst 生成） → import.meta.env（dev fallback）。

export interface OidcConfig {
  authority: string;
  clientId: string;
}

export interface AppConfig {
  /** BFF の基点 URL（例: dev は "/bff"、本番はエッジの BFF URL）。 */
  bffBaseUrl: string;
  oidc: OidcConfig;
}

declare global {
  interface Window {
    __APP_CONFIG__?: Partial<AppConfig>;
  }
}

function fromEnv(): AppConfig {
  return {
    bffBaseUrl: import.meta.env.VITE_BFF_BASE_URL ?? '/bff',
    oidc: {
      authority:
        import.meta.env.VITE_OIDC_AUTHORITY ??
        'http://localhost:8080/realms/knowledge-platform',
      clientId: import.meta.env.VITE_OIDC_CLIENT_ID ?? 'spa-web',
    },
  };
}

/** 実行時 config を解決する。window.__APP_CONFIG__ を env より優先し、欠落項目は env で補う。 */
export function loadAppConfig(win: Window = window): AppConfig {
  const env = fromEnv();
  const injected = win.__APP_CONFIG__ ?? {};
  return {
    bffBaseUrl: injected.bffBaseUrl ?? env.bffBaseUrl,
    oidc: {
      authority: injected.oidc?.authority ?? env.oidc.authority,
      clientId: injected.oidc?.clientId ?? env.oidc.clientId,
    },
  };
}

let cached: AppConfig | null = null;

/** アプリ全体で共有する実行時 config（初回のみ解決してキャッシュ）。 */
export function appConfig(): AppConfig {
  cached ??= loadAppConfig();
  return cached;
}

/** テスト用: キャッシュを破棄する。 */
export function resetAppConfigCache(): void {
  cached = null;
}
