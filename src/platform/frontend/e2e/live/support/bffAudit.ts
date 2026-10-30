// SC-22 / NFR-18 / IADR-0453 / IADR-0659（#2240 / E-54b）: live-rw が **BFF の監査ログを読む口**（読み取りだけ）。
//
// - 監査は BFF の構造化ログの 1 行（`Audit: action=… subject=… outcome=… detail=…`。`AuditLogger`）であり、画面にも API にも出ない。
//   `kubectl logs` で BFF の全 Pod（ラベル `app=bff-service`）の全コンテナを読む。
// - 🔴 kubectl の context は引数で `k3d-<E2E_STACK>` に固定する（current-context に頼らない。`support/mailpit.ts` と同じ）。層 `rw` の門
//   （`readiness.ts` の assertDisposableStack）が、`E2E_STACK` が使い捨てのスタックであることを先に確かめている。
// - 🔴 **ログの中身は返さない・出さない。** 返すのは監査の行のうち指定の action のものと、指定の文字列（書いた値）がログのどこかに
//   在るかの真偽だけである。例外の文言にも本文を載せない。
import { spawnSync } from 'node:child_process';
import type { LiveEnv } from './env';

const NAMESPACE = 'microservices-platform';
const SELECTOR = 'app=bff-service';

function bffLogs(cfg: Pick<LiveEnv, 'stack'>): string {
  if (!cfg.stack) throw new Error('BFF のログを読むには E2E_STACK（使い捨てのスタック）が要る');
  const r = spawnSync(
    'kubectl',
    [
      '--context',
      `k3d-${cfg.stack}`,
      '-n',
      NAMESPACE,
      'logs',
      '-l',
      SELECTOR,
      '--all-containers=true',
      // 🔴 `-l` の既定は各コンテナの末尾 10 行だけである。全部を読む。
      '--tail=-1',
      '--since=30m',
    ],
    { encoding: 'utf8', timeout: 60_000, maxBuffer: 256 * 1024 * 1024 },
  );
  if (r.status !== 0) {
    throw new Error(
      `BFF のログを読めない: ${(r.stderr || r.error?.message || '').trim().slice(0, 300)}`,
    );
  }
  return r.stdout;
}

export interface AuditProbe {
  /** `action=<action>` を含む監査の行（`Audit:` から行末まで）。 */
  lines: string[];
  /** 探した文字列（書いた値）がログのどこかに在るか。 */
  leaked: boolean;
}

/** 監査の行のうち `action` のものと、`secret` がログのどこかに在るかを返す（ログの中身は返さない）。 */
export function probeBffAudit(
  cfg: Pick<LiveEnv, 'stack'>,
  action: string,
  secret: string,
): AuditProbe {
  const logs = bffLogs(cfg);
  const lines = logs
    .split('\n')
    .map((l) => {
      const at = l.indexOf('Audit: ');
      return at >= 0 ? l.slice(at) : null;
    })
    .filter((l): l is string => l !== null && l.includes(`action=${action} `));
  return { lines, leaked: logs.includes(secret) };
}
