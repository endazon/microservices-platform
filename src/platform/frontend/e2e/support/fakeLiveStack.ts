// NFR / IADR-0624（#2025）: live 層の setup と teardown を L1 で流すための**偽の BFF と Keycloak**（Node の http だけ）。
// 稼働の環境には触れない。形は本物に合わせる: BFF `/bff/auth/login` → Keycloak のログイン画面（`#username` `#password` `#kc-login`）
// → OTP 画面（`#otp`）→ BFF の callback が HttpOnly の cookie を出す → `/bff/auth/me` は名前・subject・ロール・`logoutUrl`
// （`/bff/auth/logout?sid=…`）を返す → GET のログアウトは sid の一致を見てセッションを消し、認可サーバを経て戻る。
// 🔴 秘密の値はここで作り、ここから外へ出さない（試験は名前と件数だけを見る）。
import { randomBytes } from 'node:crypto';
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import { otpFor } from '../live/support/totp';

const ROLE_MARKERS: Record<string, string[]> = {
  user: [],
  operator: ['platform-operator'],
  admin: ['platform-admin', 'platform-operator'],
  noaccess: [],
  revocable: [],
  'trading-owner': ['trading-owner'],
};

export interface FakeLiveStack {
  edgeUrl: string;
  kcUrl: string;
  /** setup に渡す資格情報（環境変数名 → 値）。 */
  credentials: Record<string, string>;
  /** 発行したセッション cookie の値（成果物に出ないことを見るため）。 */
  issued: string[];
  /** 生きているセッションの数。 */
  live: () => number;
  /** ログアウトが受け付けられた回数。 */
  logouts: () => number;
  close: () => Promise<void>;
}

/** HTML の本文・属性へ入れる値をエスケープする（`& < > " '`）。要求から来た値は必ずこれを通す。 */
const escapeHtml = (value: string) =>
  value.replace(
    /[&<>"']/g,
    (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c] ?? c,
  );

/** 本文は固定の文字列か、`escapeHtml` を通した値だけで組む。 */
const html = (res: ServerResponse, body: string, status = 200) => {
  res.writeHead(status, { 'content-type': 'text/html; charset=utf-8' });
  res.end(`<!doctype html><html lang="ja"><body>${body}</body></html>`);
};
const form = (req: IncomingMessage) =>
  new Promise<URLSearchParams>((resolve) => {
    let d = '';
    req.on('data', (c) => (d += c));
    req.on('end', () => resolve(new URLSearchParams(d)));
  });
const listen = (server: Server) =>
  new Promise<number>((resolve) =>
    server.listen(0, '127.0.0.1', () => resolve((server.address() as AddressInfo).port)),
  );

/**
 * `refuseLogoutFor` の役割はログアウトを 500 で断る（teardown の失敗の経路を見るため）。
 * `requiredActionFor` の役割は OTP の後に Keycloak の必須アクション（OTP の登録）の画面へ送る（#2026 の初回の失敗の形）。
 */
export async function startFakeLiveStack(
  opts: { refuseLogoutFor?: string; requiredActionFor?: string } = {},
): Promise<FakeLiveStack> {
  const credentials: Record<string, string> = {};
  const secrets: Record<string, { password: string; totp: string }> = {};
  for (const role of Object.keys(ROLE_MARKERS)) {
    const key = role.toUpperCase().replace(/-/g, '_');
    const password = `Pw-${randomBytes(8).toString('hex')}A1`;
    const totp = randomBytes(16).toString('hex');
    secrets[role] = { password, totp };
    credentials[['E2E', key, 'PASSWORD'].join('_')] = password;
    credentials[['E2E', key, 'TOTP', 'SECRET'].join('_')] = totp;
  }
  const sessions = new Map<string, { role: string; sid: string }>();
  const issued: string[] = [];
  let logouts = 0;
  const edge = createServer();
  const kc = createServer();
  const edgePort = await listen(edge);
  const kcPort = await listen(kc);
  const edgeUrl = `http://127.0.0.1:${edgePort}`;
  // Keycloak は別のオリジンにする（setup は origin でログイン画面への遷移を待つ）。
  const kcUrl = `http://localhost:${kcPort}`;

  edge.on('request', (req, res) => {
    const u = new URL(req.url ?? '/', edgeUrl);
    const cookie = /msp-session=([^;]+)/.exec(req.headers.cookie ?? '')?.[1];
    const session = cookie ? sessions.get(cookie) : undefined;
    if (u.pathname === '/bff/auth/me') {
      if (!session) {
        res.writeHead(401);
        return res.end();
      }
      res.writeHead(200, { 'content-type': 'application/json' });
      return res.end(
        JSON.stringify({
          name: 'E2E',
          subject: `sub-${session.role}`,
          roles: ['default-roles-platform', ...ROLE_MARKERS[session.role]],
          logoutUrl: `/bff/auth/logout?sid=${session.sid}`,
        }),
      );
    }
    if (u.pathname === '/bff/auth/login') {
      res.writeHead(302, { location: `${kcUrl}/login` });
      return res.end();
    }
    if (u.pathname === '/bff/auth/callback') {
      const role = u.searchParams.get('code') ?? '';
      const value = randomBytes(24).toString('hex');
      sessions.set(value, { role, sid: randomBytes(8).toString('hex') });
      issued.push(value);
      res.writeHead(302, {
        'set-cookie': `msp-session=${value}; Path=/; HttpOnly; SameSite=Lax`,
        location: '/',
      });
      return res.end();
    }
    if (u.pathname === '/bff/auth/logout') {
      // 本物と同じく GET ＋ sid の一致（一致しなければ 400）。
      if (session && session.role === opts.refuseLogoutFor) {
        res.writeHead(500);
        return res.end();
      }
      if (!cookie || !session || u.searchParams.get('sid') !== session.sid) {
        res.writeHead(400);
        return res.end();
      }
      sessions.delete(cookie);
      logouts += 1;
      res.writeHead(302, {
        'set-cookie': 'msp-session=; Path=/; Max-Age=0',
        location: `${kcUrl}/logout?redirect=${encodeURIComponent(`${edgeUrl}/`)}`,
      });
      return res.end();
    }
    return html(res, '<main><h1>SPA</h1></main>');
  });

  kc.on('request', async (req, res) => {
    const u = new URL(req.url ?? '/', kcUrl);
    if (u.pathname === '/realms/platform/.well-known/openid-configuration') {
      res.writeHead(200, { 'content-type': 'application/json' });
      return res.end('{"issuer":"fake"}');
    }
    if (u.pathname === '/realms/platform/login-actions/required-action') {
      return html(
        res,
        '<form id="kc-totp-settings-form" method="post"><button type="submit">送信</button></form>',
      );
    }
    if (u.pathname === '/logout') {
      // 戻り先は BFF のオリジンに限る（それ以外は BFF の根へ戻す）。開いたリダイレクトにしない。
      let back = `${edgeUrl}/`;
      try {
        const target = new URL(u.searchParams.get('redirect') ?? '', edgeUrl);
        if (target.origin === new URL(edgeUrl).origin) back = target.href;
      } catch {
        // 読めない値は既定へ
      }
      res.writeHead(302, { location: back });
      return res.end();
    }
    if (u.pathname === '/login' && req.method === 'GET') {
      return html(
        res,
        '<form id="kc-form-login" method="post" action="/login"><label for="username">ID</label><input id="username" name="username">' +
          '<label for="password">PW</label><input id="password" name="password" type="password"><button id="kc-login" type="submit">ログイン</button></form>',
      );
    }
    if (u.pathname === '/login') {
      const f = await form(req);
      const role = (f.get('username') ?? '').replace(/^e2e-/, '');
      if (!secrets[role] || secrets[role].password !== f.get('password'))
        return html(res, 'ログインに失敗しました', 401);
      res.writeHead(302, { location: `/otp?u=${encodeURIComponent(role)}` });
      return res.end();
    }
    if (u.pathname === '/otp' && req.method === 'GET') {
      const role = u.searchParams.get('u') ?? '';
      return html(
        res,
        `<form method="post" action="/otp?u=${escapeHtml(encodeURIComponent(role))}"><label for="otp">コード</label><input id="otp" name="otp">` +
          '<button id="kc-login" type="submit">ログイン</button></form>',
      );
    }
    if (u.pathname === '/otp') {
      const f = await form(req);
      const role = u.searchParams.get('u') ?? '';
      const now = Date.now();
      const ok = [now, now - 30_000].some(
        (t) => secrets[role] && otpFor(secrets[role].totp, t) === f.get('otp'),
      );
      if (!ok) return html(res, 'コードが違います', 401);
      if (role === opts.requiredActionFor) {
        res.writeHead(302, {
          location: '/realms/platform/login-actions/required-action?execution=CONFIGURE_TOTP',
        });
        return res.end();
      }
      res.writeHead(302, {
        location: `${edgeUrl}/bff/auth/callback?code=${encodeURIComponent(role)}`,
      });
      return res.end();
    }
    res.writeHead(404);
    res.end();
  });

  return {
    edgeUrl,
    kcUrl,
    credentials,
    issued,
    live: () => sessions.size,
    logouts: () => logouts,
    close: async () => {
      await new Promise<void>((r) => edge.close(() => r()));
      await new Promise<void>((r) => kc.close(() => r()));
    },
  };
}
