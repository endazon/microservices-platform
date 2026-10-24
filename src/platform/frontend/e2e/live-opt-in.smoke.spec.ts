import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import { credentialNames, missingCredentials, parseLiveEnv, ROLES } from './live/support/env';
import { otpFor, remainingSeconds } from './live/support/totp';

// NFR / IADR-0569 決定 4 / IADR-0624（#2025）: live 層の config（`playwright.live.config.ts`）の **opt-in と門**を L1 で固定する。
// 稼働の環境には 1 度も触れない（到達先は誰も listen していない 127.0.0.1:9）。ブラウザも開かない。

const FRONTEND = fileURLToPath(new URL('..', import.meta.url));

/** live の config を子プロセスで起動する。E2E_* は親から引き継がない（与えたものだけ）。 */
function runLive(env: Record<string, string>, extra: string[] = []) {
  const base = Object.fromEntries(
    Object.entries(process.env).filter(([k]) => !k.startsWith('E2E_')),
  ) as Record<string, string>;
  const r = spawnSync(
    process.execPath,
    [
      fileURLToPath(import.meta.resolve('@playwright/test/cli')),
      'test',
      '--config',
      'playwright.live.config.ts',
      '--pass-with-no-tests',
      ...extra,
    ],
    { cwd: FRONTEND, env: { ...base, ...env }, encoding: 'utf8', timeout: 120_000 },
  );
  return { status: r.status, out: `${r.stdout}\n${r.stderr}` };
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

test('資格情報の欠けは名前だけを返す・到達先はオリジンだけを受ける', () => {
  const adminSecret = envName('ADMIN', 'TOTP', 'SECRET');
  expect(missingCredentials({ ...ALL_CREDENTIALS, [adminSecret]: '' })).toEqual([adminSecret]);
  expect(credentialNames('trading-owner')).toEqual({
    password: envName('TRADING', 'OWNER', 'PASSWORD'),
    totpSecret: envName('TRADING', 'OWNER', 'TOTP', 'SECRET'),
  });
  expect(() =>
    parseLiveEnv({ E2E_LIVE: '1', E2E_EDGE_URL: 'https://localhost/app' }, '/x'),
  ).toThrow(/オリジンだけ/);
});

test('TOTP は生の秘密の base32 で計算する（Keycloak の secretData.value は生の文字列。IADR-0570）', () => {
  // RFC 6238 Appendix B の SHA-1 の秘密は ASCII "12345678901234567890"（生の文字列）。T=59 → 94287082 の下 6 桁。
  expect(otpFor('12345678901234567890', 59_000)).toBe('287082');
  expect(remainingSeconds(59_000)).toBe(1);
  expect(remainingSeconds(60_000)).toBe(30);
});
