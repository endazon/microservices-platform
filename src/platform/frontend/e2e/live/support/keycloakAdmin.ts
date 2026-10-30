// NFR-10 / SC-14 / ADR-0026 / IADR-0662（#2040 / E-81）: **使い捨てのスタックの Keycloak の管理 API** を叩く口。
// M-005（初回の TOTP 登録）の一時アカウントを作って消すためだけに使う。
//
// - 🔴 **使い捨てのスタックの中だけ**。kubectl の context は引数で `k3d-<E2E_STACK>` に固定する（current-context に頼らない）。
//   向け先が使い捨てのスタックであることは、`E2E_DISPOSABLE=1` のとき globalSetup の門（`readiness.ts` の assertDisposableStack）が先に確かめている。
// - 管理 API はエッジで拒んでいる（`deploy/local/edge/keycloak-admin-deny.yaml`）ので、`kubectl port-forward svc/keycloak` で
//   クラスタの中の `http://keycloak:8080` と同じ口へ当たる（realm の後追い〔`reconcile-realm.js`〕と同じ口・同じ発行者）。
//   port-forward は自分で起こした子プロセスで、`close()` がその子だけを止める。
// - 管理者の資格情報は Secret `platform-infra/keycloak-admin` から読む（realm の後追いの Job と同じ単一情報源）。
//   🔴 値はログ・attach・例外の文言へ出さない。トークンも出さない。
import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { request, type APIRequestContext } from '@playwright/test';
import type { LiveEnv } from './env';

const NAMESPACE = 'platform-infra';
const SERVICE = 'svc/keycloak';
const SERVICE_PORT = 8080;
const ADMIN_SECRET = 'keycloak-admin';

/** `kubectl port-forward` の出力から、割り当てられたローカルポートを読む（`scripts/check-bff-multi-replica-session.js` と同じ形）。 */
export function parsePortForwardPort(text: string): number | null {
  const m = /Forwarding from 127\.0\.0\.1:(\d+)\s*->/.exec(text);
  return m ? Number(m[1]) : null;
}

function kubectlArgs(stack: string, ...rest: string[]): string[] {
  return ['--context', `k3d-${stack}`, '-n', NAMESPACE, ...rest];
}

/** Secret の 1 つのキーを読む（値は返すだけで出さない）。 */
function secretValue(stack: string, key: string): string {
  const r = spawnSync(
    'kubectl',
    kubectlArgs(stack, 'get', 'secret', ADMIN_SECRET, '-o', `jsonpath={.data.${key}}`),
    { encoding: 'utf8', timeout: 30_000 },
  );
  if (r.status !== 0 || !r.stdout) {
    throw new Error(
      `Secret ${NAMESPACE}/${ADMIN_SECRET} の ${key} を読めない: ${(r.stderr || r.error?.message || '').trim().slice(0, 300)}`,
    );
  }
  return Buffer.from(r.stdout, 'base64').toString('utf8');
}

/** port-forward を起こし、ローカルポートが決まるまで待つ。 */
async function startPortForward(stack: string): Promise<{ child: ChildProcess; port: number }> {
  const child = spawn('kubectl', kubectlArgs(stack, 'port-forward', SERVICE, `:${SERVICE_PORT}`), {
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let out = '';
  const port = await new Promise<number>((resolve, reject) => {
    const timer = setTimeout(() => {
      child.kill();
      reject(
        new Error(`port-forward ${SERVICE} が 30 秒で口を開かない: ${out.trim().slice(0, 300)}`),
      );
    }, 30_000);
    const onData = (d: Buffer) => {
      out += d.toString();
      const p = parsePortForwardPort(out);
      if (p) {
        clearTimeout(timer);
        resolve(p);
      }
    };
    child.stdout?.on('data', onData);
    child.stderr?.on('data', onData);
    child.on('exit', (code) => {
      clearTimeout(timer);
      reject(
        new Error(
          `port-forward ${SERVICE} が終わった（exit ${code}）: ${out.trim().slice(0, 300)}`,
        ),
      );
    });
  });
  return { child, port };
}

export interface KeycloakAdmin {
  /** realm の利用者を作り、その id を返す。 */
  createUser(user: Record<string, unknown>): Promise<string>;
  /** 利用者の表現（requiredActions など）を読む。 */
  getUser(id: string): Promise<{ id: string; username: string; requiredActions?: string[] }>;
  /** 利用者の資格情報の種類（`password` / `otp` / `recovery-authn-codes` など）を読む。値は読まない。 */
  credentialTypes(id: string): Promise<string[]>;
  /** 利用者を消す（404 は消えた扱い）。 */
  deleteUser(id: string): Promise<void>;
  /** port-forward と要求の文脈を閉じる。 */
  close(): Promise<void>;
}

/**
 * 使い捨てのスタックの Keycloak の管理 API を開く。`cfg.disposable` と `cfg.stack` を要る（無ければ例外。何も起こさない）。
 */
export async function openKeycloakAdmin(
  cfg: Pick<LiveEnv, 'disposable' | 'stack'>,
  realm: string,
): Promise<KeycloakAdmin> {
  if (!cfg.disposable || !cfg.stack) {
    throw new Error(
      'Keycloak の管理 API は E2E_DISPOSABLE=1 と E2E_STACK（使い捨てのスタック）のときだけ開く',
    );
  }
  const username = secretValue(cfg.stack, 'username');
  const password = secretValue(cfg.stack, 'password');
  const { child, port } = await startPortForward(cfg.stack);
  const base = `http://127.0.0.1:${port}`;
  let ctx: APIRequestContext;
  try {
    ctx = await request.newContext({ baseURL: base });
  } catch (e) {
    child.kill();
    throw e;
  }
  // 管理者のトークンは寿命が短い（master の既定は 60 秒）。試験は OTP の周期を待つので、**呼ぶたびに取り直す**。
  const authHeader = async (): Promise<Record<string, string>> => {
    const res = await ctx.post('/realms/master/protocol/openid-connect/token', {
      form: { grant_type: 'password', client_id: 'admin-cli', username, password },
      failOnStatusCode: false,
    });
    const token =
      res.status() === 200
        ? ((await res.json()) as { access_token?: string }).access_token
        : undefined;
    if (!token) throw new Error(`管理者のトークンを取れない（${res.status()}）`);
    return { authorization: `Bearer ${token}` };
  };
  const path = (rest: string) => `/admin/realms/${realm}/${rest}`;
  const fail = async (what: string, res: { status(): number; text(): Promise<string> }) =>
    new Error(`${what}: ${res.status()} ${(await res.text()).slice(0, 200)}`);
  // 最初に 1 回取って、口と資格情報が使えることを確かめる（作る前に止まる）。
  try {
    await authHeader();
  } catch (e) {
    await ctx.dispose();
    child.kill();
    throw e;
  }
  return {
    async createUser(user) {
      const res = await ctx.post(path('users'), {
        data: user,
        headers: await authHeader(),
        failOnStatusCode: false,
      });
      if (res.status() !== 201) throw await fail('POST /users', res);
      const location = res.headers()['location'] ?? '';
      const id = location.split('/').pop() ?? '';
      if (!id) throw new Error('POST /users の Location から id を読めない');
      return id;
    },
    async getUser(id) {
      const res = await ctx.get(path(`users/${id}`), {
        headers: await authHeader(),
        failOnStatusCode: false,
      });
      if (res.status() !== 200) throw await fail(`GET /users/${id}`, res);
      return (await res.json()) as { id: string; username: string; requiredActions?: string[] };
    },
    async credentialTypes(id) {
      const res = await ctx.get(path(`users/${id}/credentials`), {
        headers: await authHeader(),
        failOnStatusCode: false,
      });
      if (res.status() !== 200) throw await fail(`GET /users/${id}/credentials`, res);
      return ((await res.json()) as { type: string }[]).map((c) => c.type).sort();
    },
    async deleteUser(id) {
      const res = await ctx.delete(path(`users/${id}`), {
        headers: await authHeader(),
        failOnStatusCode: false,
      });
      if (res.status() !== 204 && res.status() !== 404)
        throw await fail(`DELETE /users/${id}`, res);
    },
    async close() {
      await ctx.dispose();
      child.kill();
    },
  };
}
