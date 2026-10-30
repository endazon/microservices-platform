// NFR / ADR-0090 / IADR-0569 決定 4 / IADR-0624（#2025）: live 層の**前提（門）**。`playwright.live.config.ts` の
// `globalSetup` として、live のときだけ全 project より先に 1 回走る。
//
// 確かめること（いずれも読み取りだけ。ログインはしない）:
//   1. 試験アカウントの資格情報の環境変数 12 個が揃っている（**名前だけ**を報告し、値は読まない・出さない）
//   2. Keycloak の discovery（`/realms/platform/.well-known/openid-configuration`）が 200
//   3. BFF の `/bff/auth/me` が 401（未認証の正しい応答。200 は別のセッションを掴んでいる、5xx は BFF が起きていない）
//
// 🔴 満たさなければ **exit 2（前提未整備）**で終える。skip の緑（何も確かめていない）とも、失敗の赤（製品の不具合）とも
// 区別する（`check-stack-ready.js --live` と同じ扱い）。全 project を skip にして緑にする形は採らない。
import { spawnSync } from 'node:child_process';
import { closeSync, existsSync, openSync, chmodSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { request } from '@playwright/test';
import { missingCredentials, parseLiveEnv, REALM, type LiveEnv } from './env';

/** 前提未整備の終了コード（IADR-0569 決定 4）。 */
export const NOT_READY_EXIT_CODE = 2;

type Probe = (url: string) => Promise<number | string>;

/** 門の判定（純関数に近い形。`probe` は URL の HTTP 状態か、届かなかった理由を返す）。不成立の理由を並べて返す。 */
export async function readinessProblems(
  cfg: LiveEnv,
  env: Record<string, string | undefined>,
  probe: Probe,
): Promise<string[]> {
  const problems: string[] = [];
  const missing = missingCredentials(env);
  if (missing.length) {
    problems.push(
      `試験アカウントの資格情報の環境変数が無い: ${missing.join(', ')}（値は Secret platform-infra/e2e-accounts。e2e/live/env.e2e.example）`,
    );
  }
  const discovery = `${cfg.kcUrl}/realms/${REALM}/.well-known/openid-configuration`;
  const kc = await probe(discovery);
  if (kc !== 200) problems.push(`Keycloak の discovery が 200 でない: ${discovery} → ${kc}`);
  const me = `${cfg.edgeUrl}/bff/auth/me`;
  const bff = await probe(me);
  if (bff !== 401)
    problems.push(
      `BFF の /bff/auth/me が 401 でない: ${me} → ${bff}（未認証で 401 を返すのが正しい）`,
    );
  return problems;
}

/** 使い捨てのスタックの判定（sweep と同じ単一の実装。#2031 / IADR-0630 決定 1）。 */
const DISPOSABLE_STACK_SCRIPT = fileURLToPath(
  new URL('../../../../../../scripts/lib/disposable-stack.js', import.meta.url),
);

/**
 * 🔴 **live-rw の門**（#2031 / IADR-0630）: 層に `rw` があれば、`E2E_STACK` が使い捨てのスタックで、kubectl の current-context が
 * `k3d-<stack>` であることを確かめる。違えば**例外で止める**（前提未整備の exit 2 ではない —— 向け先の取り違えは製品の不具合でも
 * 環境の未整備でもなく、流してはならない実行である）。`rw` が無ければ何もしない。`run` は試験が差し替える。
 * ［2026-10-30 / #2040 / IADR-0662］`E2E_DISPOSABLE=1`（使い捨てのスタックでしか流せない試験。M-005 の一時アカウント）でも同じ門を通す。
 */
export function assertDisposableStack(
  cfg: LiveEnv,
  run: (args: string[]) => { status: number | null; stderr: string } = (args) => {
    const r = spawnSync(process.execPath, args, { encoding: 'utf8' });
    return { status: r.status, stderr: r.stderr ?? r.error?.message ?? '' };
  },
): void {
  if (!cfg.layers.includes('rw') && !cfg.disposable) return;
  if (!cfg.stack)
    throw new Error('E2E_LAYER に rw があるか E2E_DISPOSABLE=1 なのに E2E_STACK が無い');
  const r = run([DISPOSABLE_STACK_SCRIPT, '--stack', cfg.stack]);
  if (r.status !== 0) {
    throw new Error(
      `[e2e-live] live-rw と E2E_DISPOSABLE=1 の試験は使い捨てのスタックの中だけで流す。止めた: ${r.stderr.trim() || `exit ${r.status}`}`,
    );
  }
}

/**
 * 発行値の記録先（`E2E_ISSUED_SECRETS_FILE`）を**実行の初めに空で作る**（0600。在れば中身を変えない）。成果物の走査は、指定した
 * ファイルが無ければ赤にするので、「何も発行しなかった」（空）と「記録先が欠けた」（無い）を区別できる（#2237 のレビュー）。
 */
export function prepareIssuedSecretsFile(cfg: Pick<LiveEnv, 'issuedSecretsFile'>): void {
  if (!cfg.issuedSecretsFile) return;
  if (!existsSync(cfg.issuedSecretsFile)) closeSync(openSync(cfg.issuedSecretsFile, 'a', 0o600));
  chmodSync(cfg.issuedSecretsFile, 0o600);
}

export default async function globalSetup(): Promise<void> {
  const cfg = parseLiveEnv(process.env, '');
  if (!cfg.live) return;
  // 書き込みの向け先の門は、到達の確かめ（GET）より前に置く（取り違えた向け先へは何も送らない）。
  assertDisposableStack(cfg);
  prepareIssuedSecretsFile(cfg);
  // config はワーカーごとにも読み込まれるので、1 回だけ出す警告はここ（globalSetup は 1 回）に置く。
  if (cfg.insecureTls) {
    process.stdout.write(
      '::warning::[e2e-live] E2E_INSECURE_TLS=1: 証明書を検証せずに流す（ルート CA を信頼させられない環境に限る）\n',
    );
  }
  const ctx = await request.newContext({ ignoreHTTPSErrors: cfg.insecureTls });
  const probe: Probe = async (url) => {
    try {
      const res = await ctx.get(url, { maxRedirects: 0, timeout: 15_000, failOnStatusCode: false });
      return res.status();
    } catch (e) {
      return `届かない（${e instanceof Error ? e.message.split('\n')[0] : String(e)}）`;
    }
  };
  const problems = await readinessProblems(cfg, process.env, probe);
  await ctx.dispose();
  if (problems.length) {
    process.stderr.write(
      `[e2e-live] 前提未整備（exit ${NOT_READY_EXIT_CODE}）。test は 1 件も流していない:\n  - ${problems.join('\n  - ')}\n`,
    );
    process.exit(NOT_READY_EXIT_CODE);
  }
  process.stdout.write(
    `[e2e-live] 前提を確かめた: ${cfg.edgeUrl}（BFF 401）・${cfg.kcUrl}（discovery 200）・資格情報 12 個\n`,
  );
}
