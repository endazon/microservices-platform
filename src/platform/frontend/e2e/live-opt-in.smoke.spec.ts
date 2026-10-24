import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import {
  credentialNames,
  missingCredentials,
  OPTION_NAMES,
  parseLiveEnv,
  ROLES,
} from './live/support/env';
import { nextPeriodOtp, otpFor, remainingSeconds, wrongOtpFor } from './live/support/totp';
import { startFakeLiveStack } from './support/fakeLiveStack';

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
  expect(parseLiveEnv({ E2E_LIVE: '1', E2E_LAYER: 'ro,rw,ro' }, '/tmp/x').layers).toEqual([
    'ro',
    'rw',
  ]);
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
  const code = await nextPeriodOtp(raw, now);
  expect(code).toBe(otpFor(raw, 60_000));
  expect(code).not.toBe(otpFor(raw, base));
});
