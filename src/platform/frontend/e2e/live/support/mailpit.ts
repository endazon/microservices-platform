// SC-15 / NFR-12 / ADR-0045 / IADR-0634（#2034 / E-54）: live-rw の**捕捉用 MTA（mailpit）を読む口**（読み取りだけ）。
//
// - 🔴 mailpit の API はエッジへ出していない（中身はリセットリンク＝認証資格。`deploy/local/infra/mailpit.yaml`）。
//   `scripts/check-password-reset-mail.js` と同じく、**Pod の中の loopback** を `kubectl exec … wget` で読む。
// - 🔴 kubectl の context は引数で `k3d-<E2E_STACK>` に固定する（current-context に頼らない）。層 `rw` の門（`readiness.ts` の
//   assertDisposableStack）が、`E2E_STACK` が使い捨てのスタックであることを先に確かめている。PoC・本番へは向けない。
// - メールを消す操作はしない（busybox の wget は DELETE を打てない。使い捨てのスタックごと消える）。
// - 🔴 返す本文にはリセットリンクが載る。呼ぶ側は読んだ直後に `recordIssuedSecret` で記録し、ログ・attach・例外の文言へ出さない。
import { spawnSync } from 'node:child_process';
import type { LiveEnv } from './env';

/** mailpit の置き場（`deploy/local/infra/mailpit.yaml` の namespace・Deployment 名・HTTP の port）。 */
const NAMESPACE = 'platform-infra';
const DEPLOY = 'mailpit';
const HTTP_PORT = 8025;

export interface CapturedMail {
  id: string;
  subject: string;
  text: string;
  recipients: string[];
  attachments: number;
}

function mailApi(cfg: Pick<LiveEnv, 'stack'>, apiPath: string): unknown {
  if (!cfg.stack) throw new Error('mailpit を読むには E2E_STACK（使い捨てのスタック）が要る');
  const r = spawnSync(
    'kubectl',
    [
      '--context',
      `k3d-${cfg.stack}`,
      '-n',
      NAMESPACE,
      'exec',
      `deploy/${DEPLOY}`,
      '--',
      'wget',
      '-q',
      '-O-',
      '-T',
      '15',
      `http://127.0.0.1:${HTTP_PORT}${apiPath}`,
    ],
    { encoding: 'utf8', timeout: 30_000 },
  );
  // 本文は出さない（リセットリンクが載りうる）。stderr は kubectl / wget の診断だけ。
  if (r.status !== 0) {
    throw new Error(
      `mailpit API を読めない（${apiPath.split('?')[0]}）: ${(r.stderr || r.error?.message || '').trim().slice(0, 300)}`,
    );
  }
  try {
    return JSON.parse(r.stdout) as unknown;
  } catch {
    throw new Error(`mailpit API の応答が JSON ではない（${apiPath.split('?')[0]}）`);
  }
}

/** 受け取った全メールの件数。 */
export function mailCount(cfg: Pick<LiveEnv, 'stack'>): number {
  const info = mailApi(cfg, '/api/v1/info') as { Messages?: unknown };
  const n = Number(info.Messages);
  if (!Number.isInteger(n) || n < 0) throw new Error('mailpit の件数（Messages）が読めない');
  return n;
}

/** 新しい順に `limit` 通（一覧の項目だけ。本文は `readMail`）。 */
export function latestMailIds(cfg: Pick<LiveEnv, 'stack'>, limit: number): string[] {
  const list = mailApi(cfg, `/api/v1/messages?limit=${limit}`) as {
    messages?: { ID?: unknown }[];
  };
  return (list.messages ?? []).map((m) => String(m.ID ?? '')).filter(Boolean);
}

/** 1 通を読む。🔴 `text` にはリセットリンクが載る（呼ぶ側が記録し、出さない）。 */
export function readMail(cfg: Pick<LiveEnv, 'stack'>, id: string): CapturedMail {
  const m = mailApi(cfg, `/api/v1/message/${encodeURIComponent(id)}`) as {
    Subject?: unknown;
    Text?: unknown;
    To?: { Address?: unknown }[];
    Attachments?: unknown[];
  };
  return {
    id,
    subject: String(m.Subject ?? ''),
    text: String(m.Text ?? ''),
    recipients: (m.To ?? []).map((t) => String(t.Address ?? '')),
    attachments: Array.isArray(m.Attachments) ? m.Attachments.length : 0,
  };
}
