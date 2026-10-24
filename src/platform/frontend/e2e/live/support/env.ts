// NFR / IADR-0569 決定 4 / IADR-0624（#2025）: live 層の opt-in と到達先を環境変数から決める（純関数）。
//
// - `E2E_LIVE`: **未設定・空は opt-out**（全 project を 0 件・exit 0）、**ちょうど `1` だけが live**、それ以外は例外。
//   例示（`env.e2e.example` の `<set-me>`）を写したまま直し忘れたときや `true` と書いたときに、黙って 0 件の緑にしない。
// - `E2E_LAYER` は `ro` / `rw` / `trading` / `llm` のカンマ区切り。未設定は `ro`。
//   🔴 値域外の値、および `E2E_LIVE=1` 無しの `E2E_LAYER` は**例外**にする（config の読み込みで失敗させる）。
//   綴り違いが黙って 0 件の緑になるのを防ぐ（ADR-0090「全 skip を緑にしない」）。
// - 資格情報（`E2E_<ROLE>_PASSWORD` / `E2E_<ROLE>_TOTP_SECRET`）の**値はここで読まない**。名前だけを持ち、
//   readiness 門が「揃っているか」を、setup が値を読む。

const LAYERS = ['ro', 'rw', 'trading', 'llm'] as const;
export type Layer = (typeof LAYERS)[number];

/** 試験アカウントの役割（realm `platform` の `e2e-<role>`。IADR-0570）。環境変数の接頭辞は大文字・`-`→`_`。 */
export const ROLES = [
  'user',
  'operator',
  'admin',
  'noaccess',
  'revocable',
  'trading-owner',
] as const;
export type Role = (typeof ROLES)[number];

const DEFAULT_EDGE_URL = 'https://localhost';
const DEFAULT_KC_URL = 'https://keycloak.localhost';
export const REALM = 'platform';

type Env = Record<string, string | undefined>;

/** 資格情報のほかに live の config が読む環境変数（**空は未設定と同じ**に扱う）。 */
export const OPTION_NAMES = [
  'E2E_LIVE',
  'E2E_LAYER',
  'E2E_EDGE_URL',
  'E2E_KC_URL',
  'E2E_INSECURE_TLS',
  'E2E_AUTH_DIR',
  'E2E_SEARCH_HITS',
  'E2E_KEEP_SESSIONS',
] as const;

export interface LiveEnv {
  live: boolean;
  layers: Layer[];
  edgeUrl: string;
  kcUrl: string;
  insecureTls: boolean;
  /** storageState の置き場。レポートの外（gitignore）。 */
  authDir: string;
  /**
   * `E2E_SEARCH_HITS=1`: 検索の索引が在る（seed 文書が取り込まれ、埋め込みが供給されている）環境で、**ヒットを判定する**
   * （IADR-0627）。未設定なら検索は「200 ＋描画」だけを見て、ヒットを要る test は理由つきで skip する。
   * `verify-oidc-edge-flow.sh` の `SEARCH_HITS=1` と同じ意味（索引が空であり得る環境で、空を故障と取り違えない）。
   */
  searchHits: boolean;
  /**
   * `E2E_KEEP_SESSIONS=trading-owner`: teardown がこの役割のセッションを失効させずに残す（state は 0600）。後始末は AST の live の
   * teardown が引き受ける（#2231 / IADR-0628。AST/IADR-0588）。**値域は `trading-owner` だけ**。既定（未設定）は全部失効。
   */
  keepSessions: Role[];
}

/** teardown が残してよい役割（AST の live が読む取引オーナーだけ。他の役割は必ず失効させる）。 */
export const KEEPABLE_ROLES = ['trading-owner'] as const satisfies readonly Role[];

/** 役割から環境変数名を作る（`trading-owner` → `E2E_TRADING_OWNER_PASSWORD`）。 */
export function credentialNames(role: Role): { password: string; totpSecret: string } {
  const key = role.toUpperCase().replace(/-/g, '_');
  return { password: `E2E_${key}_PASSWORD`, totpSecret: `E2E_${key}_TOTP_SECRET` };
}

/** 例示（`env.e2e.example`）の置き場所の印。写したまま値を入れ忘れたものを未設定として扱う。 */
const PLACEHOLDER = '<set-me>';

/**
 * 6 役割 × 2 の環境変数名のうち、未設定のもの（空・空白だけ・例示の `<set-me>` のまま）。値は返さない。
 * 🔴 `<set-me>` のまま門を通すと、実の Keycloak へ 6 回の失敗ログインを流し、brute force 保護で試験アカウントがロックされうる。
 */
export function missingCredentials(env: Env): string[] {
  return ROLES.flatMap((role) => Object.values(credentialNames(role))).filter((name) => {
    const value = (env[name] ?? '').trim();
    return value === '' || value === PLACEHOLDER;
  });
}

function originOf(name: string, value: string): string {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error(`${name} が URL ではない: ${value}`);
  }
  if (url.pathname !== '/' || url.search || url.hash) {
    throw new Error(`${name} はオリジンだけを書く（パス・クエリを付けない）: ${value}`);
  }
  return url.origin;
}

export function parseLiveEnv(env: Env, defaultAuthDir: string): LiveEnv {
  // 空は未設定と同じ（`E2E_LAYER=` と書いて既定の ro を選ぶ、子プロセスへ空で渡して手元の .env.e2e を効かせない、の両方が要る）。
  const read = (name: (typeof OPTION_NAMES)[number]): string | undefined => {
    const v = env[name];
    return v === undefined || v === '' ? undefined : v;
  };
  const rawLive = read('E2E_LIVE') ?? '';
  if (rawLive !== '' && rawLive !== '1') {
    throw new Error(
      `E2E_LIVE の値域外: ${rawLive}（live にするなら 1、しないなら未設定か空。例示の <set-me> を写したままにしない）`,
    );
  }
  const live = rawLive === '1';
  const rawLayer = read('E2E_LAYER');
  if (rawLayer !== undefined && !live) {
    throw new Error(
      'E2E_LAYER が設定されているのに E2E_LIVE=1 が無い（live 層は明示の opt-in でだけ流す）',
    );
  }
  const layers = (rawLayer ?? 'ro')
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s !== '');
  if (layers.length === 0)
    throw new Error('E2E_LAYER が空である（ro / rw / trading / llm のカンマ区切り）');
  for (const layer of layers) {
    if (!(LAYERS as readonly string[]).includes(layer)) {
      throw new Error(`E2E_LAYER の値域外: ${layer}（ro / rw / trading / llm のカンマ区切り）`);
    }
  }
  const rawHits = read('E2E_SEARCH_HITS') ?? '';
  if (rawHits !== '' && rawHits !== '1') {
    throw new Error(
      `E2E_SEARCH_HITS の値域外: ${rawHits}（ヒットを判定するなら 1、しないなら未設定か空）`,
    );
  }
  if (rawHits === '1' && !live) {
    throw new Error(
      'E2E_SEARCH_HITS が設定されているのに E2E_LIVE=1 が無い（live 層は明示の opt-in でだけ流す）',
    );
  }
  const rawKeep = read('E2E_KEEP_SESSIONS') ?? '';
  if (rawKeep !== '' && !(KEEPABLE_ROLES as readonly string[]).includes(rawKeep)) {
    throw new Error(
      `E2E_KEEP_SESSIONS の値域外: ${rawKeep}（残せるのは ${KEEPABLE_ROLES.join(' / ')} だけ。全部を残す指定は無い）`,
    );
  }
  if (rawKeep !== '' && !live) {
    throw new Error(
      'E2E_KEEP_SESSIONS が設定されているのに E2E_LIVE=1 が無い（live 層は明示の opt-in でだけ流す）',
    );
  }
  return {
    live,
    layers: [...new Set(layers)] as Layer[],
    edgeUrl: originOf('E2E_EDGE_URL', read('E2E_EDGE_URL') ?? DEFAULT_EDGE_URL),
    kcUrl: originOf('E2E_KC_URL', read('E2E_KC_URL') ?? DEFAULT_KC_URL),
    insecureTls: read('E2E_INSECURE_TLS') === '1',
    authDir: read('E2E_AUTH_DIR') ?? defaultAuthDir,
    searchHits: rawHits === '1',
    keepSessions: rawKeep === '' ? [] : [rawKeep as Role],
  };
}

/** 役割の storageState のファイル名（`<authDir>/<role>.json`）。 */
export function stateFile(authDir: string, role: Role): string {
  return `${authDir.replace(/\/+$/, '')}/${role}.json`;
}
