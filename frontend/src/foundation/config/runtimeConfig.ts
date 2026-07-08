// Issue #126: 実行時 config の読み込み。ビルド成果物を環境非依存に保ち、接続先（BFF・Keycloak）を
// デプロイ時に切り替えられるようにする（バックエンドとフロントの疎結合。BFF/OpenAPI が境界）。
// 優先順: window.__APP_CONFIG__（public/config.js・本番は envsubst 生成） → import.meta.env（dev fallback）。

export interface OidcConfig {
  authority: string;
  clientId: string;
}

// Issue #136 / SC-10: 運用ダッシュボードから開く外部可観測性ツールの入口 URL。環境ごとに異なり
// デプロイ時に注入する（環境非依存ビルド）。未設定のツールは画面に導線を出さない（Kiali は未配備など）。
export interface OpsLinks {
  grafanaUrl?: string;
  jaegerUrl?: string;
  kialiUrl?: string;
}

export interface AppConfig {
  /** BFF の基点 URL（例: dev は "/bff"、本番はエッジの BFF URL）。 */
  bffBaseUrl: string;
  oidc: OidcConfig;
  /** SC-10 の外部ツール導線（任意。未設定項目は非表示）。 */
  opsLinks: OpsLinks;
}

declare global {
  interface Window {
    __APP_CONFIG__?: Partial<AppConfig>;
  }
}

// 空文字（envsubst で未定義変数が空に置換された場合）は「未設定」として扱う。
function orUndef(v: string | undefined): string | undefined {
  return v && v.length > 0 ? v : undefined;
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
    opsLinks: {
      grafanaUrl: orUndef(import.meta.env.VITE_GRAFANA_URL),
      jaegerUrl: orUndef(import.meta.env.VITE_JAEGER_URL),
      kialiUrl: orUndef(import.meta.env.VITE_KIALI_URL),
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
    opsLinks: {
      grafanaUrl: orUndef(injected.opsLinks?.grafanaUrl) ?? env.opsLinks.grafanaUrl,
      jaegerUrl: orUndef(injected.opsLinks?.jaegerUrl) ?? env.opsLinks.jaegerUrl,
      kialiUrl: orUndef(injected.opsLinks?.kialiUrl) ?? env.opsLinks.kialiUrl,
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
