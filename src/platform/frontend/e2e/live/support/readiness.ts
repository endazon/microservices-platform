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

export default async function globalSetup(): Promise<void> {
  const cfg = parseLiveEnv(process.env, '');
  if (!cfg.live) return;
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
