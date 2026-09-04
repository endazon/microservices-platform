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
  /**
   * Wiki.js 管理 UI の到達先（dev のみ）。**画面は読まない**（#1200 / ADR-0073 決定 1・2: SC-04 は `/bff/wiki/*`
   * 経由でページツリー・本文・検索を描き、stg/prod では `WIKI_BASE_URL` を設定しないことが統制）。
   * 項目は dev の手引き（`deploy/local/values-local.yaml`）のために残してある。
   */
  wikiBaseUrl?: string;
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
      authority: import.meta.env.VITE_OIDC_AUTHORITY ?? 'http://localhost:8080/realms/platform',
      clientId: import.meta.env.VITE_OIDC_CLIENT_ID ?? 'platform-spa',
    },
    opsLinks: {
      grafanaUrl: orUndef(import.meta.env.VITE_GRAFANA_URL),
      jaegerUrl: orUndef(import.meta.env.VITE_JAEGER_URL),
      kialiUrl: orUndef(import.meta.env.VITE_KIALI_URL),
    },
    wikiBaseUrl: orUndef(import.meta.env.VITE_WIKI_BASE_URL),
  };
}

// NFR, ADR-0032, IADR-0273, #439: BFF セッション方式は資格情報を **HttpOnly Cookie** で運ぶ。
// ブラウザが Cookie を付けるのは同一オリジンの要求だけであり（`apiClient` は `credentials: 'same-origin'`）、
// BFF は CORS を許可していない（それが CSRF ヘッダを 2 枚目の壁として成立させている前提でもある）。
//
// 🔴 **別オリジンを指す `bffBaseUrl` を注入すると、全要求が静かに未認証になる** —— 応答は 401 で、
// 画面はログインへ飛び、また 401 になる。エラーの形が「壊れている」ではなく「ログインし直して」なので、
// 設定ミスだと気付くまでに時間がかかる。**Bearer 方式のときは成立していた構成であり、移行で意味が変わった。**
// したがってここで**起動時に落とす**（deny-by-default。静かに縮退させない）。
export function assertSameOriginBffBaseUrl(bffBaseUrl: string, pageOrigin: string): void {
  // 相対パスは常に同一オリジン（本番・dev の既定はこちら）。
  if (bffBaseUrl.startsWith('/') && !bffBaseUrl.startsWith('//')) return;

  let origin: string;
  try {
    origin = new URL(bffBaseUrl, pageOrigin).origin;
  } catch {
    throw new Error(`bffBaseUrl が URL として解釈できない: ${bffBaseUrl}`);
  }
  if (origin !== pageOrigin) {
    throw new Error(
      `bffBaseUrl は画面と同一オリジンでなければならない（BFF セッションの Cookie が送られない）: ` +
        `bffBaseUrl=${bffBaseUrl} / 画面=${pageOrigin}`,
    );
  }
}

/** 実行時 config を解決する。window.__APP_CONFIG__ を env より優先し、欠落項目は env で補う。 */
export function loadAppConfig(win: Window = window): AppConfig {
  const env = fromEnv();
  const injected = win.__APP_CONFIG__ ?? {};
  const bffBaseUrl = injected.bffBaseUrl ?? env.bffBaseUrl;
  assertSameOriginBffBaseUrl(bffBaseUrl, win.location.origin);
  return {
    bffBaseUrl,
    oidc: {
      authority: injected.oidc?.authority ?? env.oidc.authority,
      clientId: injected.oidc?.clientId ?? env.oidc.clientId,
    },
    opsLinks: {
      grafanaUrl: orUndef(injected.opsLinks?.grafanaUrl) ?? env.opsLinks.grafanaUrl,
      jaegerUrl: orUndef(injected.opsLinks?.jaegerUrl) ?? env.opsLinks.jaegerUrl,
      kialiUrl: orUndef(injected.opsLinks?.kialiUrl) ?? env.opsLinks.kialiUrl,
    },
    wikiBaseUrl: orUndef(injected.wikiBaseUrl) ?? env.wikiBaseUrl,
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
