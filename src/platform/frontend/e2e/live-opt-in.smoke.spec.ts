import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:http';
import { mkdtempSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import {
  credentialNames,
  credentialsOf,
  missingCredentials,
  OPTION_NAMES,
  parseLiveEnv,
  ROLES,
} from './live/support/env';
import { nextPeriodOtp, otpFor, remainingSeconds, wrongOtpFor } from './live/support/totp';
import { ja, jaPattern, parsePo } from './live/support/catalog';
import { startFakeLiveStack } from './support/fakeLiveStack';
import { revokeSessions, runSweep, sweepArgs, SWEEP_ROLES } from './live/support/teardown';
import { createCleanup, creatorRole, e2eName } from './live/support/cleanup';
import { assertDisposableStack, prepareIssuedSecretsFile } from './live/support/readiness';
import { openKeycloakAdmin, parsePortForwardPort } from './live/support/keycloakAdmin';
import { createRequire } from 'node:module';
import { recordIssuedSecret } from './live/support/issuedSecrets';
import { normalizeRows } from './live/support/dictionaries';
import { liveProjects } from './live/support/projects';
import { submitOtp, submitPassword } from './live/support/login';
import { stateFile } from './live/support/env';
import {
  createLlmBudget,
  isLlmCall,
  LLM_CALLS_PER_RUN,
  LLM_CALLS_PER_TEST,
} from './live/support/llmBudget';

// NFR / IADR-0569 決定 4 / IADR-0624（#2025）: live 層の config（`playwright.live.config.ts`）の **opt-in と門**を L1 で固定する。
// 稼働の環境には 1 度も触れない（到達先は誰も listen していない 127.0.0.1:9）。ブラウザも開かない。

const FRONTEND = fileURLToPath(new URL('..', import.meta.url));

/**
 * live の config が読む環境変数をすべて**空**にしたもの。外すだけでは足りない —— config は手元の `e2e/live/.env.e2e` を
 * `process.loadEnvFile` で読み、**未設定の名前にだけ**値を入れる。空で渡しておけば手元のファイルがあっても試験の結果が変わらない
 * （config は空を未設定と同じに扱う）。
 */
function blankLiveEnv(): Record<string, string> {
  const names = [...OPTION_NAMES, ...ROLES.flatMap((role) => Object.values(credentialNames(role)))];
  return Object.fromEntries(names.map((n) => [n, '']));
}

/** live の config を子プロセスで起動する。E2E_* は親から引き継がない（既定で空。与えたものだけが値を持つ）。 */
function runLive(env: Record<string, string>, extra: string[] = []) {
  const base = {
    ...(Object.fromEntries(
      Object.entries(process.env).filter(([k]) => !k.startsWith('E2E_')),
    ) as Record<string, string>),
    ...blankLiveEnv(),
  };
  // 子の実行の成果物は一時ディレクトリへ（親の L1 の test-results / レポートを消させない・汚さない）。
  const out = mkdtempSync(join(tmpdir(), 'e2e-live-out-'));
  try {
    const r = spawnSync(
      process.execPath,
      [
        fileURLToPath(import.meta.resolve('@playwright/test/cli')),
        'test',
        '--config',
        'playwright.live.config.ts',
        '--pass-with-no-tests',
        '--reporter',
        'list',
        '--output',
        out,
        ...extra,
      ],
      {
        cwd: FRONTEND,
        env: { ...base, NO_PROXY: '127.0.0.1,localhost', no_proxy: '127.0.0.1,localhost', ...env },
        encoding: 'utf8',
        timeout: 120_000,
      },
    );
    return { status: r.status, out: `${r.stdout}\n${r.stderr}` };
  } finally {
    rmSync(out, { recursive: true, force: true });
  }
}

/**
 * runLive の非同期版。偽の BFF と Keycloak は**この試験のプロセス**で listen するので、子を同期で待つと応答できない。
 */
async function runLiveAsync(
  env: Record<string, string>,
): Promise<{ status: number | null; out: string }> {
  const out = mkdtempSync(join(tmpdir(), 'e2e-live-out-'));
  const base = {
    ...(Object.fromEntries(
      Object.entries(process.env).filter(([k]) => !k.startsWith('E2E_')),
    ) as Record<string, string>),
    ...blankLiveEnv(),
  };
  try {
    return await new Promise((resolve, reject) => {
      const child = spawn(
        process.execPath,
        [
          fileURLToPath(import.meta.resolve('@playwright/test/cli')),
          'test',
          '--config',
          'playwright.live.config.ts',
          '--pass-with-no-tests',
          '--reporter',
          'list',
          '--output',
          out,
          // 偽の BFF と Keycloak は SPA を配らないので、層の spec（`*.ro.spec.ts` など）は流さない。setup と teardown だけを見る。
          '--project',
          'live-setup',
        ],
        {
          cwd: FRONTEND,
          env: {
            ...base,
            NO_PROXY: '127.0.0.1,localhost',
            no_proxy: '127.0.0.1,localhost',
            ...env,
          },
        },
      );
      let text = '';
      child.stdout.on('data', (d) => (text += d));
      child.stderr.on('data', (d) => (text += d));
      child.on('error', reject);
      child.on('close', (status) => resolve({ status, out: text }));
    });
  } finally {
    rmSync(out, { recursive: true, force: true });
  }
}

/** 環境変数の名前を組み立てる（名前と値の区別が付かない走査器が、鍵名の隣の長い英大文字列を鍵と取り違えないように）。 */
const envName = (...parts: string[]) => ['E2E', ...parts].join('_');

const ALL_CREDENTIALS = Object.fromEntries(
  ROLES.flatMap((role) => Object.values(credentialNames(role))).map((n) => [n, 'x']),
);

test('E2E_LIVE が未設定・空なら 0 件で exit 0（稼働の環境へ当たらない）', () => {
  for (const env of [{}, { E2E_LIVE: '' }] as Record<string, string>[]) {
    const r = runLive(env, ['--list']);
    expect(r.status, r.out).toBe(0);
    expect(r.out).toContain('E2E_LIVE=1 が無いので 0 件で終わる');
    expect(r.out).toMatch(/Total: 0 tests/);
  }
});

test('E2E_LIVE が 1 でも空でもない値（例示の <set-me>・true）は読み込みで失敗する（黙って 0 件の緑にしない）', () => {
  for (const value of ['<set-me>', 'true']) {
    const r = runLive({ E2E_LIVE: value }, ['--list']);
    expect(r.status, r.out).not.toBe(0);
    expect(r.out).toContain(`E2E_LIVE の値域外: ${value}`);
  }
});

test('E2E_LIVE=1 無しの E2E_LAYER、値域外の E2E_LAYER は config の読み込みで失敗する（0 件の緑にしない）', () => {
  const noLive = runLive({ E2E_LAYER: 'ro' }, ['--list']);
  expect(noLive.status, noLive.out).not.toBe(0);
  expect(noLive.out).toContain('E2E_LIVE=1 が無い');
  const typo = runLive({ E2E_LIVE: '1', E2E_LAYER: 'ro,rx' }, ['--list']);
  expect(typo.status, typo.out).not.toBe(0);
  expect(typo.out).toContain('E2E_LAYER の値域外: rx');
});

test('E2E_LIVE=1 なら live-setup が 6 役割のログインを持ち、既定の層は ro だけ', () => {
  const r = runLive({ E2E_LIVE: '1' }, ['--list']);
  expect(r.status, r.out).toBe(0);
  expect(
    r.out.match(/\[live-setup\] › auth\.setup\.ts:\d+:\d+ › e2e-[a-z-]+ でログインし/g),
  ).toHaveLength(ROLES.length);
  // live-ro の spec（#2027 以降）は live-ro の project に載り、setup の project には載らない。
  expect(r.out).toMatch(/\[live-ro\] › shell\.ro\.spec\.ts:/);
  expect(r.out).toMatch(/\[live-ro\] › auth-errors\.ro\.spec\.ts:/);
  expect(r.out).not.toMatch(/\[live-setup\] › [a-z-]+\.ro\.spec\.ts:/);
  expect(parseLiveEnv({ E2E_LIVE: '1' }, '/tmp/x').layers).toEqual(['ro']);
  expect(
    parseLiveEnv({ E2E_LIVE: '1', E2E_LAYER: 'ro,rw,ro', E2E_STACK: 'e2e-live' }, '/tmp/x').layers,
  ).toEqual(['ro', 'rw']);
  // rw は ro の後に流れる（同じ実行で ro を選んだとき。IADR-0630 決定 4）。
  const both = runLive({ E2E_LIVE: '1', E2E_LAYER: 'ro,rw', E2E_STACK: 'e2e-live' }, ['--list']);
  expect(both.status, both.out).toBe(0);
  expect(both.out).toMatch(/\[live-rw\] › private-notes\.rw\.spec\.ts:/);
});

test('前提（資格情報・Keycloak・BFF）が無ければ exit 2（前提未整備）で、test を 1 件も流さない', () => {
  const r = runLive({
    E2E_LIVE: '1',
    E2E_EDGE_URL: 'http://127.0.0.1:9',
    E2E_KC_URL: 'http://127.0.0.1:9',
    ...ALL_CREDENTIALS,
  });
  expect(r.status, r.out).toBe(2);
  expect(r.out).toContain('前提未整備（exit 2）');
  expect(r.out).toContain('Keycloak の discovery が 200 でない');
  expect(r.out).toContain('BFF の /bff/auth/me が 401 でない');
  // 資格情報が揃っていれば、その理由は出ない（名前の列挙で値は出さない）。
  expect(r.out).not.toContain('資格情報の環境変数が無い');
});

test('例示の <set-me> を写したままの資格情報は未設定として exit 2（実の Keycloak へ失敗ログインを流さない）', () => {
  const adminPassword = envName('ADMIN', 'PASSWORD');
  const r = runLive({
    E2E_LIVE: '1',
    E2E_EDGE_URL: 'http://127.0.0.1:9',
    E2E_KC_URL: 'http://127.0.0.1:9',
    ...ALL_CREDENTIALS,
    [adminPassword]: ' <set-me> ',
  });
  expect(r.status, r.out).toBe(2);
  expect(r.out).toContain(`資格情報の環境変数が無い: ${adminPassword}（`);
  expect(r.out).not.toContain('<set-me>（');
});

test('資格情報の欠けは名前だけを返す・到達先はオリジンだけを受ける', () => {
  const adminSecret = envName('ADMIN', 'TOTP', 'SECRET');
  expect(missingCredentials({ ...ALL_CREDENTIALS, [adminSecret]: '' })).toEqual([adminSecret]);
  expect(missingCredentials({ ...ALL_CREDENTIALS, [adminSecret]: '<set-me>' })).toEqual([
    adminSecret,
  ]);
  expect(missingCredentials({ ...ALL_CREDENTIALS, [adminSecret]: '   ' })).toEqual([adminSecret]);
  expect(missingCredentials(ALL_CREDENTIALS)).toEqual([]);
  expect(credentialNames('trading-owner')).toEqual({
    password: envName('TRADING', 'OWNER', 'PASSWORD'),
    totpSecret: envName('TRADING', 'OWNER', 'TOTP', 'SECRET'),
  });
  expect(() =>
    parseLiveEnv({ E2E_LIVE: '1', E2E_EDGE_URL: 'https://localhost/app' }, '/x'),
  ).toThrow(/オリジンだけ/);
});

test('偽の BFF と Keycloak に対して、setup が 6 役割でログインし、teardown が 6 回ログアウトして state を消す', async () => {
  const fake = await startFakeLiveStack();
  const authDir = mkdtempSync(join(tmpdir(), 'e2e-live-auth-'));
  try {
    const r = await runLiveAsync({
      E2E_LIVE: '1',
      E2E_EDGE_URL: fake.edgeUrl,
      E2E_KC_URL: fake.kcUrl,
      E2E_AUTH_DIR: authDir,
      ...fake.credentials,
    });
    expect(r.status, r.out).toBe(0);
    expect(r.out).toMatch(/6 passed/);
    // globalTeardown が走り、6 役割のセッションをログアウトで失効させた（同じ cookie で /me が 401 になることも teardown が見る）。
    expect(fake.logouts(), 'ログアウトの回数').toBe(ROLES.length);
    expect(fake.live(), '生きているセッション').toBe(0);
    expect(r.out).toContain(
      '[e2e-live] セッションを失効させた: user, operator, admin, noaccess, revocable, trading-owner',
    );
    // state のファイルは残らない。
    expect(readdirSync(authDir)).toEqual([]);
    // 出力に cookie の値も資格情報の値も出ない。
    for (const secret of [...fake.issued, ...Object.values(fake.credentials)])
      expect(r.out).not.toContain(secret);
  } finally {
    await fake.close();
    rmSync(authDir, { recursive: true, force: true });
  }
});

test('ログアウトに失敗した役割があれば、名前だけを出して exit ≠ 0（state は消す）', async () => {
  const fake = await startFakeLiveStack({ refuseLogoutFor: 'admin' });
  const authDir = mkdtempSync(join(tmpdir(), 'e2e-live-auth-'));
  try {
    const r = await runLiveAsync({
      E2E_LIVE: '1',
      E2E_EDGE_URL: fake.edgeUrl,
      E2E_KC_URL: fake.kcUrl,
      E2E_AUTH_DIR: authDir,
      ...fake.credentials,
    });
    expect(r.status, r.out).not.toBe(0);
    expect(r.out).toContain('セッションを失効させられなかった役割');
    expect(r.out).toContain('e2e-admin:');
    expect(fake.logouts()).toBe(ROLES.length - 1);
    expect(readdirSync(authDir)).toEqual([]);
    for (const secret of [...fake.issued, ...Object.values(fake.credentials)])
      expect(r.out).not.toContain(secret);
  } finally {
    await fake.close();
    rmSync(authDir, { recursive: true, force: true });
  }
});

test('OTP の後に Keycloak が必須アクションを求めたら、待ち続けずにそう言って落ちる（#2026 の初回の失敗の形）', async () => {
  const fake = await startFakeLiveStack({ requiredActionFor: 'user' });
  const authDir = mkdtempSync(join(tmpdir(), 'e2e-live-auth-'));
  try {
    const started = Date.now();
    const r = await runLiveAsync({
      E2E_LIVE: '1',
      E2E_EDGE_URL: fake.edgeUrl,
      E2E_KC_URL: fake.kcUrl,
      E2E_AUTH_DIR: authDir,
      ...fake.credentials,
    });
    expect(r.status, r.out).not.toBe(0);
    expect(r.out).toContain(
      'Keycloak が必須アクションを求めている（フォーム: kc-totp-settings-form）',
    );
    // 失敗した段の attach は origin と path だけ（query の state・code・sid を出さない）。BFF の入口の状態と Location の origin も出す。
    expect(r.out).toContain(`url: ${fake.kcUrl}/realms/platform/login-actions/required-action`);
    expect(r.out).not.toContain('execution=');
    // Keycloak の画面の表示文言・フォーム・OTP の欄の有無（入力値は読まない）。
    expect(r.out).toContain(
      'keycloak messages: アカウントを有効にするには認証アプリを設定してください',
    );
    expect(r.out).toContain('keycloak forms: kc-totp-settings-form');
    expect(r.out).toContain('#otp field: 無い');
    expect(r.out).not.toMatch(/[?&](state|code|session_state)=/);
    // 60 秒の timeout まで待たない（ほかの 5 役割と teardown を含めて短く終わる）。
    expect(Date.now() - started).toBeLessThan(45_000);
  } finally {
    await fake.close();
    rmSync(authDir, { recursive: true, force: true });
  }
});

test('process.loadEnvFile は既に在る名前（空を含む）を上書きしない（手元の .env.e2e が子プロセスの試験を変えない根拠）', () => {
  const dir = mkdtempSync(join(tmpdir(), 'e2e-live-env-'));
  try {
    const file = join(dir, '.env.e2e');
    writeFileSync(file, 'E2E_LIVE=1\nE2E_LAYER=rw\n');
    const r = spawnSync(
      process.execPath,
      [
        '-e',
        'process.loadEnvFile(process.argv[1]); console.log(JSON.stringify([process.env.E2E_LIVE, process.env.E2E_LAYER]))',
        file,
      ],
      { env: { PATH: process.env.PATH ?? '', E2E_LIVE: '' }, encoding: 'utf8' },
    );
    expect(r.status, r.stderr).toBe(0);
    // E2E_LIVE は空のまま（上書きされない）。未設定だった E2E_LAYER だけがファイルの値になる。
    expect(JSON.parse(r.stdout.trim())).toEqual(['', 'rw']);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('TOTP は生の秘密の base32 で計算する（Keycloak の secretData.value は生の文字列。IADR-0570）', () => {
  // RFC 6238 Appendix B の SHA-1 の秘密は ASCII "12345678901234567890"（生の文字列）。T=59 → 94287082 の下 6 桁。
  expect(otpFor('12345678901234567890', 59_000)).toBe('287082');
  expect(remainingSeconds(59_000)).toBe(1);
  expect(remainingSeconds(60_000)).toBe(30);
});

test('誤りの OTP は前後 1 周期の正しい値のどれとも違い、次の周期の OTP は今の周期の値と違う（IADR-0626）', async () => {
  const raw = '12345678901234567890';
  // realm の otpPolicyLookAheadWindow: 1 は前後 1 周期を受け付ける。その 3 つのどれとも一致しない。
  for (const t of [30_000, 59_000, 89_000, 1_111_111_109_000]) {
    const wrong = wrongOtpFor(raw, t);
    expect(wrong).toMatch(/^\d{6}$/);
    for (const k of [-1, 0, 1]) expect(wrong).not.toBe(otpFor(raw, t + k * 30_000));
  }
  // 周期の終わり（残り 1 秒）で呼ぶと、次の周期まで待って次の周期の値を返す（otpPolicyCodeReusable: false の対策）。
  const start = Date.now();
  const base = 59_000; // 周期の残り 1 秒
  const now = () => base + (Date.now() - start);
  const { code, wrong, atMs } = await nextPeriodOtp(raw, now);
  expect(code).toBe(otpFor(raw, 60_000));
  expect(code).not.toBe(otpFor(raw, base));
  // 誤りの値は正しい値と同じ時刻から導かれ、その時刻の前後 1 周期の窓のどの正しい値とも違う。
  expect(code).toBe(otpFor(raw, atMs));
  for (const k of [-1, 0, 1]) expect(wrong).not.toBe(otpFor(raw, atMs + k * 30_000));
});

test('E2E_SEARCH_HITS は 1 か未設定・空だけを受け、E2E_LIVE=1 無しでは読み込みで失敗する（IADR-0627）', () => {
  expect(parseLiveEnv({ E2E_LIVE: '1' }, '/tmp/x').searchHits).toBe(false);
  expect(parseLiveEnv({ E2E_LIVE: '1', E2E_SEARCH_HITS: '' }, '/tmp/x').searchHits).toBe(false);
  expect(parseLiveEnv({ E2E_LIVE: '1', E2E_SEARCH_HITS: '1' }, '/tmp/x').searchHits).toBe(true);
  expect(() => parseLiveEnv({ E2E_LIVE: '1', E2E_SEARCH_HITS: 'true' }, '/tmp/x')).toThrow(
    /E2E_SEARCH_HITS の値域外/,
  );
  expect(() => parseLiveEnv({ E2E_SEARCH_HITS: '1' }, '/tmp/x')).toThrow(/E2E_LIVE=1 が無い/);
});

test('E2E_SECRETS_LISTED は 1 か未設定・空だけを受け、E2E_LIVE=1 無しでは読み込みで失敗する（#2230）', () => {
  expect(parseLiveEnv({ E2E_LIVE: '1' }, '/tmp/x').secretsListed).toBe(false);
  expect(parseLiveEnv({ E2E_LIVE: '1', E2E_SECRETS_LISTED: '' }, '/tmp/x').secretsListed).toBe(
    false,
  );
  expect(parseLiveEnv({ E2E_LIVE: '1', E2E_SECRETS_LISTED: '1' }, '/tmp/x').secretsListed).toBe(
    true,
  );
  expect(() => parseLiveEnv({ E2E_LIVE: '1', E2E_SECRETS_LISTED: 'true' }, '/tmp/x')).toThrow(
    /E2E_SECRETS_LISTED の値域外/,
  );
  expect(() => parseLiveEnv({ E2E_SECRETS_LISTED: '1' }, '/tmp/x')).toThrow(/E2E_LIVE=1 が無い/);
});

test('live の spec の文言は ja のカタログから引き、msgid が無ければ例外、件数の文言は数字を受ける正規表現になる（IADR-0627）', () => {
  // 製品のカタログに在る msgid（SC-18 のグラフの図の名前）。
  expect(ja('起点: {originTitle}', { originTitle: '文書 A' })).toBe('起点: 文書 A');
  const figure = jaPattern(
    'ナレッジグラフ（ノード {shownCount} 件・辺 {edgeCount} 本）。詳細は凡例と選択パネルを参照',
  );
  expect('ナレッジグラフ（ノード 3 件・辺 0 本）。詳細は凡例と選択パネルを参照').toMatch(figure);
  expect('ナレッジグラフ（ノード 件・辺 0 本）。詳細は凡例と選択パネルを参照').not.toMatch(figure);
  expect(() => ja('カタログに無い文言')).toThrow(/msgid が無い/);
  // 複数行の msgid / msgstr を連結する。
  const po = parsePo(
    'msgid ""\n"前半"\n"後半"\nmsgstr ""\n"訳の前半"\n"訳の後半"\n\nmsgid "単独"\nmsgstr ""\n',
  );
  expect(po.get('前半後半')).toBe('訳の前半訳の後半');
  expect(po.get('単独')).toBe('単独');
});

test('E2E_KEEP_SESSIONS=trading-owner なら、teardown は他の 5 役割を失効させ、取引オーナーの state だけを 0600 で残す（#2231 / IADR-0628）', async () => {
  const fake = await startFakeLiveStack();
  const authDir = mkdtempSync(join(tmpdir(), 'e2e-live-auth-'));
  try {
    const r = await runLiveAsync({
      E2E_LIVE: '1',
      E2E_KEEP_SESSIONS: 'trading-owner',
      E2E_EDGE_URL: fake.edgeUrl,
      E2E_KC_URL: fake.kcUrl,
      E2E_AUTH_DIR: authDir,
      ...fake.credentials,
    });
    expect(r.status, r.out).toBe(0);
    expect(r.out).toMatch(/6 passed/);
    // 取引オーナー以外の 5 役割は失効させた（ログアウト 5 回・生きているセッションは取引オーナーの 1 つ）。
    expect(fake.logouts(), 'ログアウトの回数').toBe(ROLES.length - 1);
    expect(fake.live(), '生きているセッション').toBe(1);
    expect(r.out).toContain(
      '[e2e-live] セッションを失効させた: user, operator, admin, noaccess, revocable',
    );
    expect(r.out).toContain(
      '失効させずに残した（E2E_KEEP_SESSIONS。後始末は AST の live の teardown）: trading-owner',
    );
    // 残るのは取引オーナーの state だけで、本人だけが読める権限。
    expect(readdirSync(authDir)).toEqual(['trading-owner.json']);
    expect(statSync(join(authDir, 'trading-owner.json')).mode & 0o777).toBe(0o600);
    for (const secret of [...fake.issued, ...Object.values(fake.credentials)])
      expect(r.out).not.toContain(secret);
  } finally {
    await fake.close();
    rmSync(authDir, { recursive: true, force: true });
  }
});

test('E2E_KEEP_SESSIONS は trading-owner だけを受け、値域外と E2E_LIVE=1 無しは読み込みで失敗する（全部を残す指定は無い）', () => {
  expect(parseLiveEnv({ E2E_LIVE: '1' }, '/tmp/x').keepSessions).toEqual([]);
  expect(
    parseLiveEnv({ E2E_LIVE: '1', E2E_KEEP_SESSIONS: 'trading-owner' }, '/tmp/x').keepSessions,
  ).toEqual(['trading-owner']);
  for (const bad of ['1', 'all', 'true', 'user', 'admin', 'trading-owner,user'])
    expect(() => parseLiveEnv({ E2E_LIVE: '1', E2E_KEEP_SESSIONS: bad }, '/tmp/x')).toThrow(
      /E2E_KEEP_SESSIONS の値域外/,
    );
  expect(() => parseLiveEnv({ E2E_KEEP_SESSIONS: 'trading-owner' }, '/tmp/x')).toThrow(
    /E2E_LIVE=1 が無い/,
  );
  // 子プロセスの config の読み込みでも落ちる。
  const r = runLive({ E2E_LIVE: '1', E2E_KEEP_SESSIONS: 'all' }, ['--list']);
  expect(r.status, r.out).not.toBe(0);
  expect(r.out).toContain('E2E_KEEP_SESSIONS の値域外');
});

test('e2e-live.yml（使い捨てスタック）は E2E_KEEP_SESSIONS を使わない（既定の全部失効のまま。#2231）', () => {
  const wf = readFileSync(
    fileURLToPath(new URL('../../../../.github/workflows/e2e-live.yml', import.meta.url)),
    'utf8',
  );
  expect(wf).not.toContain('E2E_KEEP_SESSIONS');
});

test('残す役割の state の権限を変えられなくても、他の 5 役割は 5 回とも失効させ、失敗に積む（exit 1 の条件。#2232 / IADR-0628 決定 2）', async ({
  browser,
}) => {
  const fake = await startFakeLiveStack();
  const authDir = mkdtempSync(join(tmpdir(), 'e2e-live-auth-'));
  try {
    // 6 役割の state を偽の BFF と Keycloak に対して作る（setup と同じ画面の流れ。値は入力欄にだけ入れる）。
    for (const role of ROLES) {
      const names = credentialNames(role);
      const context = await browser.newContext({ baseURL: fake.edgeUrl });
      const page = await context.newPage();
      await page.goto('/bff/auth/login?returnUrl=%2F');
      await submitPassword(page, `e2e-${role}`, fake.credentials[names.password]);
      await submitOtp(page, otpFor(fake.credentials[names.totpSecret], Date.now()));
      await page.waitForURL((url) => url.origin === fake.edgeUrl);
      await context.storageState({ path: stateFile(authDir, role) });
      await context.close();
    }
    expect(fake.live(), '作ったセッション').toBe(ROLES.length);
    const cfg = {
      ...parseLiveEnv(
        {
          E2E_LIVE: '1',
          E2E_KEEP_SESSIONS: 'trading-owner',
          E2E_EDGE_URL: fake.edgeUrl,
          E2E_KC_URL: fake.kcUrl,
        },
        authDir,
      ),
    };
    const result = await revokeSessions(cfg, browser, () => {
      throw new Error('EPERM（試験で失敗させた）');
    });
    // 他の 5 役割は 5 回とも失効した。取引オーナーは権限の失敗として積まれ、globalTeardown はこれで exit 1 にする。
    expect(fake.logouts(), 'ログアウトの回数').toBe(ROLES.length - 1);
    expect(result.revoked).toEqual(['user', 'operator', 'admin', 'noaccess', 'revocable']);
    expect(result.kept).toEqual([]);
    expect(result.failed.map((f) => f.role)).toEqual(['trading-owner']);
    expect(result.failed[0].reason).toContain('0600 にできない');
  } finally {
    await fake.close();
    rmSync(authDir, { recursive: true, force: true });
  }
});

test('E2E_SWEEP は dry-run / delete だけを受け、E2E_LIVE=1 と E2E_STACK を要る（向け先を明示しなければ例外。#2030 / IADR-0629）', () => {
  const base = parseLiveEnv({ E2E_LIVE: '1' }, '/tmp/x');
  expect([base.sweep, base.stack]).toEqual(['off', null]);
  expect(sweepArgs(base)).toBeNull();
  for (const bad of ['1', 'true', 'DELETE', 'dryrun', 'off'])
    expect(() =>
      parseLiveEnv({ E2E_LIVE: '1', E2E_SWEEP: bad, E2E_STACK: 'e2e-live' }, '/tmp/x'),
    ).toThrow(/E2E_SWEEP の値域外/);
  expect(() => parseLiveEnv({ E2E_SWEEP: 'dry-run', E2E_STACK: 'e2e-live' }, '/tmp/x')).toThrow(
    /E2E_LIVE=1 が無い/,
  );
  expect(() => parseLiveEnv({ E2E_LIVE: '1', E2E_SWEEP: 'dry-run' }, '/tmp/x')).toThrow(
    /E2E_STACK が無い/,
  );
  expect(() => parseLiveEnv({ E2E_LIVE: '1', E2E_STACK: 'e2e-live' }, '/tmp/x')).toThrow(
    /E2E_SWEEP も層の rw も E2E_DISPOSABLE も無い/,
  );
  // 既定は dry-run の引数（--delete を付けない）。向け先はすべて明示で渡す。
  const dry = sweepArgs(
    parseLiveEnv({ E2E_LIVE: '1', E2E_SWEEP: 'dry-run', E2E_STACK: 'e2e-live' }, '/tmp/auth'),
  )!;
  expect(dry.slice(1)).toEqual([
    '--live',
    '--edge-url',
    'https://localhost',
    '--auth-dir',
    '/tmp/auth',
    '--stack',
    'e2e-live',
    '--dry-run',
  ]);
  expect(statSync(dry[0]).isFile(), `sweep の本体が無い: ${dry[0]}`).toBe(true);
  const del = sweepArgs(
    parseLiveEnv({ E2E_LIVE: '1', E2E_SWEEP: 'delete', E2E_STACK: 'e2e-live' }, '/tmp/auth'),
  )!;
  expect(del.at(-1)).toBe('--delete');
});

test('後始末は積んだ逆順に /bff/* へ X-MSP-CSRF つきで送り、404 は消えた扱い、失敗しても続けて未削除一覧を attach する（#2030 / IADR-0629）', async ({
  playwright,
}, testInfo) => {
  const seen: string[] = [];
  const server = createServer((req, res) => {
    seen.push(`${req.method} ${req.url} csrf=${req.headers['x-msp-csrf'] ?? '-'}`);
    const status =
      req.url === '/bff/documents/gone' ? 404 : req.url === '/bff/tags/busy' ? 409 : 204;
    res.writeHead(status).end();
  });
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', r));
  const port = (server.address() as { port: number }).port;
  const request = await playwright.request.newContext({ baseURL: `http://127.0.0.1:${port}` });
  try {
    const cleanup = createCleanup(request, '/tmp/auth/user.json');
    // 名前が e2e- で始まらない・/bff/ 以外へ送る後始末は、積む時点で例外（sweep が拾えない・画面を経由させない）。
    expect(() => cleanup.track('doc-1', { method: 'DELETE', path: '/bff/documents/1' })).toThrow(
      /e2e- で始める/,
    );
    expect(() => cleanup.track('e2e-r1-x', { method: 'DELETE', path: '/api/x' })).toThrow(
      /\/bff\/ へ直接送る/,
    );
    expect(() => cleanup.track('e2e-r1-x')).toThrow(/手順が無い/);
    cleanup.track('e2e-r1-doc', { method: 'DELETE', path: '/bff/documents/gone' });
    cleanup.track(
      'e2e-r1-note',
      { method: 'DELETE', path: '/bff/private-notes/n1' },
      { method: 'POST', path: '/bff/private-notes/purge', data: { ids: ['n1'] } },
    );
    cleanup.track(
      'e2e-r1-tag',
      { method: 'DELETE', path: '/bff/tags/busy' },
      { method: 'POST', path: '/bff/never' },
    );
    const before = testInfo.attachments.length;
    const leftovers = await cleanup.run(testInfo);
    expect(seen).toEqual([
      'DELETE /bff/tags/busy csrf=1',
      'DELETE /bff/private-notes/n1 csrf=1',
      'POST /bff/private-notes/purge csrf=1',
      'DELETE /bff/documents/gone csrf=1',
    ]);
    expect(leftovers).toEqual([
      { name: 'e2e-r1-tag', step: 'DELETE /bff/tags/busy', reason: '409' },
    ]);
    const attached = testInfo.attachments.slice(before);
    expect(attached.map((a) => a.name)).toEqual(['未削除一覧']);
    expect(JSON.parse(String(attached[0]!.body))).toEqual(leftovers);
    // 一覧から外す（attach の内容は確かめた。この試験自体の成果物に「未削除」を残さない）。
    testInfo.attachments.splice(before);
    const note = testInfo.annotations.findIndex((a) => a.type === '未削除');
    expect(note, '未削除の注釈が無い').toBeGreaterThanOrEqual(0);
    testInfo.annotations.splice(note, 1);
    // 2 回目は空（積んだものは run で空になる）。
    seen.length = 0;
    expect(await cleanup.run(testInfo)).toEqual([]);
    expect(seen).toEqual([]);
  } finally {
    await request.dispose();
    await new Promise((r) => server.close(r));
  }
});

test('E2E_SWEEP があっても要る state（6 役割）が 1 つでも無ければ、sweep を流さずに理由を 1 行出す（setup の失敗に重ねない。#2234）', () => {
  const authDir = mkdtempSync(join(tmpdir(), 'e2e-sweep-skip-'));
  try {
    const cfg = parseLiveEnv(
      { E2E_LIVE: '1', E2E_SWEEP: 'dry-run', E2E_STACK: 'e2e-live', E2E_AUTH_DIR: authDir },
      '/unused',
    );
    // setup が e2e-admin と e2e-noaccess で落ちた（その 2 役割の state が無い）。
    for (const role of ROLES.filter((r) => r !== 'admin' && r !== 'noaccess'))
      writeFileSync(stateFile(authDir, role), '{"cookies":[],"origins":[]}');
    const lines: string[] = [];
    let spawned = 0;
    const spawn = () => {
      spawned++;
      return { status: 0, stdout: '', stderr: '' };
    };
    expect(runSweep(cfg, { spawn, write: (l) => lines.push(l) })).toBe('skipped');
    expect(spawned, 'state が欠けたのに sweep を起動した').toBe(0);
    expect(lines).toEqual([
      '[e2e-live] 残骸掃きを飛ばした（state が無い役割: e2e-admin, e2e-noaccess。setup の失敗を先に見る）\n',
    ]);
    // 揃えば流す（陽性対照）。未指定なら流さない。
    for (const role of ROLES)
      writeFileSync(stateFile(authDir, role), '{"cookies":[],"origins":[]}');
    expect(runSweep(cfg, { spawn, write: () => {} })).toBe('ok');
    expect(spawned).toBe(1);
    expect(runSweep(parseLiveEnv({ E2E_LIVE: '1' }, authDir), { spawn })).toBe('off');
    expect(spawned).toBe(1);
  } finally {
    rmSync(authDir, { recursive: true, force: true });
  }
});

test('E2E_LAYER に rw があれば E2E_STACK を要り、globalSetup の門が使い捨てのスタックの判定器を呼び、違えば例外で止める（#2031 / IADR-0630）', () => {
  expect(() => parseLiveEnv({ E2E_LIVE: '1', E2E_LAYER: 'ro,rw' }, '/tmp/x')).toThrow(
    /rw があるのに E2E_STACK が無い/,
  );
  const ro = parseLiveEnv({ E2E_LIVE: '1' }, '/tmp/x');
  const rw = parseLiveEnv({ E2E_LIVE: '1', E2E_LAYER: 'ro,rw', E2E_STACK: 'e2e-live' }, '/tmp/x');
  expect(rw.stack).toBe('e2e-live');
  const calls: string[][] = [];
  // rw が無ければ判定器を呼ばない。
  assertDisposableStack(ro, (args) => (calls.push(args), { status: 0, stderr: '' }));
  expect(calls).toEqual([]);
  // rw があれば `scripts/lib/disposable-stack.js --stack <stack>` を呼ぶ。exit 0 なら通す。
  assertDisposableStack(rw, (args) => (calls.push(args), { status: 0, stderr: '' }));
  expect(calls.map((a) => a.slice(1))).toEqual([['--stack', 'e2e-live']]);
  expect(statSync(calls[0]![0]!).isFile(), `判定器が無い: ${calls[0]![0]}`).toBe(true);
  // 違えば（PoC の context）例外で止める。理由を出す。
  expect(() =>
    assertDisposableStack(rw, () => ({
      status: 2,
      stderr:
        '[disposable-stack] 止めた: kubectl の current-context が k3d-e2e-live でない（rancher-desktop）',
    })),
  ).toThrow(/使い捨てのスタックの中だけで流す。止めた: .*rancher-desktop/);
  // 本物の判定器: 一覧に無いスタックは kubectl を読む前に exit 2。
  const r = spawnSync(process.execPath, [calls[0]![0]!, '--stack', 'rancher-desktop'], {
    encoding: 'utf8',
  });
  expect(r.status, r.stderr).toBe(2);
  expect(r.stderr).toContain('使い捨てのスタックだけを受ける');
});

test('E2E_RUN_ID は英小文字と数字 4〜16 字だけを受け、名前は e2e-<runId>-<種類>-r<再試行> になる（IADR-0630 決定 3）', () => {
  expect(parseLiveEnv({ E2E_LIVE: '1', E2E_RUN_ID: 'mabc1234' }, '/tmp/x').runId).toBe('mabc1234');
  for (const bad of ['ABC1', 'ab', 'a-b-c-d', 'x'.repeat(17), 'run id'])
    expect(() => parseLiveEnv({ E2E_LIVE: '1', E2E_RUN_ID: bad }, '/tmp/x')).toThrow(
      /E2E_RUN_ID の値域外/,
    );
  expect(e2eName('mabc1234', 'note', 0)).toBe('e2e-mabc1234-note-r0');
  expect(e2eName('mabc1234', 'device', 1)).toBe('e2e-mabc1234-device-r1');
  expect(() => e2eName(null, 'note', 0)).toThrow(/E2E_RUN_ID が無い/);
  expect(() => e2eName('mabc1234', 'Note', 0)).toThrow(/英小文字/);
});

test('teardown が要る state の役割は sweep の視点の和集合と一致し、e2e-revocable の context では後始末の部品が実行時に例外で止める（IADR-0630 決定 5）', () => {
  const sweep = createRequire(import.meta.url)('../../../../scripts/e2e-live-sweep.js') as {
    KINDS: { viewers: string[] }[];
  };
  const union = [...new Set(sweep.KINDS.flatMap((k) => k.viewers))].sort();
  expect([...SWEEP_ROLES].sort()).toEqual(union);
  expect(SWEEP_ROLES).not.toContain('revocable');
  // 作る側の門: storageState のファイル名から役割を引く。e2e-revocable・役割の分からない state では作らせない。
  const request = {} as Parameters<typeof createCleanup>[0];
  expect(creatorRole(stateFile('/tmp/auth', 'user'))).toBe('user');
  expect(creatorRole(stateFile('/tmp/auth', 'trading-owner'))).toBe('trading-owner');
  expect(() => createCleanup(request, stateFile('/tmp/auth', 'revocable'))).toThrow(
    /e2e-revocable では作らない/,
  );
  expect(() => createCleanup(request, '/tmp/auth/someone.json')).toThrow(/役割が分からない/);
  expect(() => createCleanup(request, undefined)).toThrow(/役割が分からない/);
  expect(() => createCleanup(request, { cookies: [], origins: [] })).toThrow(/役割が分からない/);
});

test('発行された値（端末トークン）は 0600 のファイルへ記録され、偽の成果物に載れば走査が赤になる（値は出さない。IADR-0630 決定 6）', () => {
  const dir = mkdtempSync(join(tmpdir(), 'e2e-issued-'));
  try {
    const sink = join(dir, 'issued.env');
    const token = ['dev', 'tok', 'L1fixture', 'Zx9Yw8Vu7'].join('-');
    // 記録先が無ければ何もしない（手元の実行）。名前・値の形が違えば例外（値は文言に入れない）。
    recordIssuedSecret({ issuedSecretsFile: null }, 'E2E_ISSUED_DEVICE_TOKEN', token);
    expect(() => recordIssuedSecret({ issuedSecretsFile: sink }, 'DEVICE_TOKEN', token)).toThrow(
      /E2E_ISSUED_/,
    );
    expect(() =>
      recordIssuedSecret({ issuedSecretsFile: sink }, 'E2E_ISSUED_DEVICE_TOKEN', `${token}\nx`),
    ).toThrow(/記録できない値/);
    // globalSetup が先に空のファイル（0600）を作る。2 回呼んでも中身を変えない。
    prepareIssuedSecretsFile({ issuedSecretsFile: sink });
    expect(statSync(sink).mode & 0o777).toBe(0o600);
    expect(readFileSync(sink, 'utf8')).toBe('');
    recordIssuedSecret({ issuedSecretsFile: sink }, 'E2E_ISSUED_DEVICE_TOKEN', token);
    prepareIssuedSecretsFile({ issuedSecretsFile: sink });
    expect(statSync(sink).mode & 0o777).toBe(0o600);
    expect(readFileSync(sink, 'utf8')).toBe(`E2E_ISSUED_DEVICE_TOKEN=${token}\n`);

    // 偽の成果物と、試験アカウントの資格情報の偽の値（走査器は資格情報が 1 つも無いと例外にする）。
    const envFile = join(dir, 'accounts.env');
    writeFileSync(envFile, `E2E_ADMIN_PASSWORD=${['Pw', 'L1fixture', 'Q1w2E3r4'].join('-')}\n`);
    const scanWith = (content: string) => {
      const target = mkdtempSync(join(dir, 'report-'));
      writeFileSync(join(target, 'error-context.md'), content);
      return spawnSync(
        process.execPath,
        [
          fileURLToPath(
            new URL('../../../../scripts/check-e2e-artifact-secrets.js', import.meta.url),
          ),
          '--env-file',
          envFile,
          '--issued-file',
          sink,
          '--scan',
          target,
        ],
        { encoding: 'utf8' },
      );
    };
    const leaked = scanWith(`Error: expected row\n  - code "${token}"\n`);
    expect(leaked.status, leaked.stderr).toBe(1);
    expect(leaked.stderr).toContain('E2E_ISSUED_DEVICE_TOKEN#1');
    expect(`${leaked.stdout}${leaked.stderr}`).not.toContain(token);
    const clean = scanWith('Error: expected row\n');
    expect(clean.status, clean.stderr).toBe(0);
    expect(clean.stdout).toContain('発行値 1 個');
    // 記録先が欠けた（指定したのにファイルが無い）なら赤（0 個として黙って続けない）。
    rmSync(sink);
    const lost = scanWith('Error: expected row\n');
    expect(lost.status, lost.stderr).toBe(1);
    expect(lost.stderr).toContain('発行値のファイルが無い');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('辞書の突き合わせは e2e- の行と件数の項目を除き、id の無い行も安定したキー（id・key・name・行の JSON）で並べる（#2238 / IADR-0631 決定 4）', () => {
  const rows = [
    { name: 'b-tag', usageCount: 3 },
    { key: 'clearance', scope: 'user' },
    { id: 'p1', name: 'dev: public 取扱者は public を読める' },
    { name: 'e2e-r1-tag', usageCount: 0 },
    { layer: 'x', isSeed: true },
    { name: 'a-tag', usageCount: 1 },
  ];
  const forward = normalizeRows(rows);
  // id の無い行を含めて逆順で返されても差分にならない。件数（usageCount）が変わっても差分にならない。
  expect(normalizeRows([...rows].reverse())).toEqual(forward);
  expect(
    normalizeRows(rows.map((r) => ('usageCount' in r ? { ...r, usageCount: 99 } : r))),
  ).toEqual(forward);
  // 試験が作る行（e2e-）は比べない。中身（件数以外）の違いは差分になる。
  expect(forward.map((r) => r.name ?? r.key ?? 'json')).not.toContain('e2e-r1-tag');
  expect(forward.some((r) => 'usageCount' in r)).toBe(false);
  expect(normalizeRows([...rows, { name: 'c-tag' }])).not.toEqual(forward);
});

test('e2e-revocable を失効させる試験は専用の project で、同じ実行で選んだ live-ro・live-rw の後に流れる（#2033 / IADR-0632 決定 1）', () => {
  const deps = (layers: ('ro' | 'rw')[]) =>
    Object.fromEntries(
      liveProjects({ live: true, layers }).map((p) => [p.name, p.dependencies ?? []]),
    );
  const both = deps(['ro', 'rw']);
  expect(both['live-rw-revocation']).toEqual(['live-setup', 'live-ro', 'live-rw']);
  expect(both['live-rw']).toEqual(['live-setup', 'live-ro']);
  // rw だけでも live-rw の後。rw を選ばなければ project ごと無い。
  expect(deps(['rw'])['live-rw-revocation']).toEqual(['live-setup', 'live-rw']);
  expect(deps(['ro'])).not.toHaveProperty('live-rw-revocation');
  // live-rw は失効の試験を流さない（流すのは live-rw-revocation だけ）。
  const rw = liveProjects({ live: true, layers: ['rw'] }).find((p) => p.name === 'live-rw')!;
  const revocation = liveProjects({ live: true, layers: ['rw'] }).find(
    (p) => p.name === 'live-rw-revocation',
  )!;
  expect(String(rw.testIgnore)).toBe(String(revocation.testMatch));
  expect(String(revocation.testMatch)).toContain('revocation');
  // 実際の一覧でも、失効の試験は live-rw-revocation にだけ載る。
  const r = runLive({ E2E_LIVE: '1', E2E_LAYER: 'ro,rw', E2E_STACK: 'e2e-live' }, ['--list']);
  expect(r.status, r.out).toBe(0);
  expect(r.out).toMatch(/\[live-rw-revocation\] › revocation\.rw\.spec\.ts:/);
  expect(r.out).not.toMatch(/\[live-rw\] › revocation\.rw\.spec\.ts:/);
  expect(liveProjects({ live: false, layers: ['ro'] })).toEqual([]);
});

test('e2e-revocable のリセット申請・アカウントコンソールは live-rw で流れ、失効の project には載らない（#2034 / IADR-0634 決定 1）', () => {
  // live-rw は live-ro（auth-errors）の後、live-rw-revocation は live-rw の後（上の試験）。ここではファイルの載り先を一覧で固定する。
  const r = runLive({ E2E_LIVE: '1', E2E_LAYER: 'ro,rw', E2E_STACK: 'e2e-live' }, ['--list']);
  expect(r.status, r.out).toBe(0);
  expect(r.out).toMatch(/\[live-rw\] › account\.rw\.spec\.ts:.*M-006/);
  expect(r.out).toMatch(/\[live-rw\] › account\.rw\.spec\.ts:.*M-007/);
  expect(r.out).not.toMatch(/\[live-rw-revocation\] › account\.rw\.spec\.ts:/);
});

test('資格情報が欠けていれば Keycloak へ送る前に例外で止め、文言は変数名だけ（値を出さない。#2239 のレビュー）', () => {
  const pw = envName('REVOCABLE', 'PASSWORD');
  const otp = envName('REVOCABLE', 'TOTP', 'SECRET');
  const value = ['L1', 'fixture', 'value', 'Zx9'].join('-');
  expect(credentialsOf('revocable', { [pw]: value, [otp]: value })).toEqual({
    password: value,
    totpSecret: value,
  });
  for (const env of [
    {},
    { [pw]: value },
    { [pw]: '', [otp]: value },
    { [pw]: '   ', [otp]: value },
    { [pw]: '<set-me>', [otp]: value },
  ]) {
    let message = '';
    try {
      credentialsOf('revocable', env);
    } catch (e) {
      message = (e as Error).message;
    }
    expect(message, JSON.stringify(Object.keys(env))).toMatch(
      /e2e-revocable の資格情報の環境変数が無い/,
    );
    expect(message).not.toContain(value);
  }
});

// NFR-10 / SC-14 / IADR-0662（#2040 / E-81）: 使い捨てのスタックでしか流せない試験（M-005 の初回の TOTP 登録）の門。
test('E2E_DISPOSABLE は 1 か未設定・空だけを受け、E2E_LIVE=1 と E2E_STACK を要る（#2040 / IADR-0662）', () => {
  expect(parseLiveEnv({ E2E_LIVE: '1' }, '/tmp/x').disposable).toBe(false);
  expect(parseLiveEnv({ E2E_LIVE: '1', E2E_DISPOSABLE: '' }, '/tmp/x').disposable).toBe(false);
  const on = parseLiveEnv({ E2E_LIVE: '1', E2E_DISPOSABLE: '1', E2E_STACK: 'e2e-live' }, '/tmp/x');
  expect([on.disposable, on.stack]).toEqual([true, 'e2e-live']);
  for (const bad of ['true', '0', 'yes'])
    expect(() =>
      parseLiveEnv({ E2E_LIVE: '1', E2E_DISPOSABLE: bad, E2E_STACK: 'e2e-live' }, '/tmp/x'),
    ).toThrow(/E2E_DISPOSABLE の値域外/);
  expect(() => parseLiveEnv({ E2E_DISPOSABLE: '1', E2E_STACK: 'e2e-live' }, '/tmp/x')).toThrow(
    /E2E_LIVE=1 が無い/,
  );
  expect(() => parseLiveEnv({ E2E_LIVE: '1', E2E_DISPOSABLE: '1' }, '/tmp/x')).toThrow(
    /E2E_DISPOSABLE=1 なのに E2E_STACK が無い/,
  );
});

test('E2E_DISPOSABLE=1 なら層に rw が無くても globalSetup の門が使い捨てのスタックの判定器を呼ぶ（#2040 / IADR-0662）', () => {
  const calls: string[][] = [];
  const ro = parseLiveEnv({ E2E_LIVE: '1' }, '/tmp/x');
  assertDisposableStack(ro, (args) => (calls.push(args), { status: 0, stderr: '' }));
  expect(calls).toEqual([]);
  const disposable = parseLiveEnv(
    { E2E_LIVE: '1', E2E_DISPOSABLE: '1', E2E_STACK: 'e2e-live' },
    '/tmp/x',
  );
  assertDisposableStack(disposable, (args) => (calls.push(args), { status: 0, stderr: '' }));
  expect(calls.map((a) => a.slice(1))).toEqual([['--stack', 'e2e-live']]);
  expect(() =>
    assertDisposableStack(disposable, () => ({
      status: 2,
      stderr:
        '[disposable-stack] 止めた: kubectl の current-context が k3d-e2e-live でない（rancher-desktop）',
    })),
  ).toThrow(/使い捨てのスタックの中だけで流す。止めた: .*rancher-desktop/);
});

test('M-005（初回の TOTP 登録）は E2E_DISPOSABLE=1 のときだけ live-ro に載り、常設の PoC（未設定）では 0 件（#2040 / IADR-0662）', () => {
  const poc = runLive({ E2E_LIVE: '1' }, ['--list']);
  expect(poc.status, poc.out).toBe(0);
  expect(poc.out).toMatch(/\[live-ro\] › shell\.ro\.spec\.ts:/);
  expect(poc.out).not.toMatch(/totp-enroll\.ro\.spec\.ts/);
  const disposable = runLive({ E2E_LIVE: '1', E2E_DISPOSABLE: '1', E2E_STACK: 'e2e-live' }, [
    '--list',
  ]);
  expect(disposable.status, disposable.out).toBe(0);
  expect(disposable.out.match(/\[live-ro\] › totp-enroll\.ro\.spec\.ts:.*M-005/g)).toHaveLength(2);
  // 管理 API の口は門の外では開かない（kubectl を 1 度も起こさずに例外）。
  return expect(
    openKeycloakAdmin({ disposable: false, stack: 'e2e-live' }, 'platform'),
  ).rejects.toThrow(/E2E_DISPOSABLE=1 と E2E_STACK/);
});

test('port-forward の出力からローカルポートを読み、読めなければ null（#2040 / IADR-0662）', () => {
  expect(parsePortForwardPort('Forwarding from 127.0.0.1:43123 -> 8080\n')).toBe(43123);
  expect(parsePortForwardPort('Forwarding from [::1]:43123 -> 8080\n')).toBeNull();
  expect(parsePortForwardPort('error: unable to forward')).toBeNull();
});

// SC-01, SC-08, NFR / IADR-0665（#2035 / E-70）: llm 層の**上限の門**。1 実行の LLM 呼び出しは 1 試験 1 回 × 試験 2 件 × retries 0 = 2 以下
// （基準書 §6.2）。稼働の環境には触れない（`--list` だけ）。試験を 1 件足す・上限を変える・retries を足す変更はここで赤になる。
test('llm 層の上限は 1 試験 1 回・1 実行 2 回で、数えるのは POST /bff/search と POST /bff/analysis/* だけ（IADR-0665）', () => {
  expect(LLM_CALLS_PER_TEST).toBe(1);
  expect(LLM_CALLS_PER_RUN).toBe(2);
  expect(isLlmCall('POST', '/bff/search')).toBe(true);
  expect(isLlmCall('POST', '/bff/analysis/ask/stream')).toBe(true);
  expect(isLlmCall('POST', '/bff/analysis/analyze')).toBe(true);
  expect(isLlmCall('POST', '/bff/analysis/ask')).toBe(true);
  // 数えない口（陰性）: 読み取りの GET・候補（索引から引く）・名前が前方一致するだけの別の口。
  expect(isLlmCall('GET', '/bff/search')).toBe(false);
  expect(isLlmCall('POST', '/bff/attribute-values')).toBe(false);
  expect(isLlmCall('POST', '/bff/search-trends')).toBe(false);
  expect(isLlmCall('POST', '/bff/analysisx')).toBe(false);
  // 上限の内は通し、超えた要求は通さずに記録する（後段へ届けない）。
  const budget = createLlmBudget(LLM_CALLS_PER_TEST);
  expect(budget.admit('POST /bff/analysis/ask/stream')).toBe(true);
  expect(budget.admit('POST /bff/search')).toBe(false);
  expect(budget.admit('POST /bff/analysis/analyze')).toBe(false);
  expect(budget.admitted()).toEqual(['POST /bff/analysis/ask/stream']);
  expect(budget.refused()).toEqual(['POST /bff/search', 'POST /bff/analysis/analyze']);
  expect(() => createLlmBudget(0)).toThrow(/上限/);
});

test('E2E_LAYER=llm の --list は project llm にちょうど 2 件（M-031・M-052）で、retries は 0。既定の層には llm の試験が載らない（IADR-0665）', () => {
  const llm = runLive({ E2E_LIVE: '1', E2E_LAYER: 'llm' }, ['--list']);
  expect(llm.status, llm.out).toBe(0);
  const listed = llm.out.match(/\[llm\] › [^\n]+/g) ?? [];
  expect(listed, llm.out).toHaveLength(LLM_CALLS_PER_RUN / LLM_CALLS_PER_TEST);
  expect(listed.filter((l) => /rag\.llm\.spec\.ts:.*M-031/.test(l))).toHaveLength(1);
  expect(listed.filter((l) => /rag\.llm\.spec\.ts:.*M-052/.test(l))).toHaveLength(1);
  const project = liveProjects({ live: true, layers: ['llm'] }).find((p) => p.name === 'llm')!;
  expect(project.retries).toBe(0);
  expect(project.workers).toBe(1);
  const ro = runLive({ E2E_LIVE: '1' }, ['--list']);
  expect(ro.status, ro.out).toBe(0);
  expect(ro.out).not.toMatch(/\[llm\]|\.llm\.spec\.ts/);
});
