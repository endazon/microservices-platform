// NFR / NFR-18 / IADR-0630 決定 6（#2031 / E-51）: live の実行の中で**発行された値**（SC-20 の端末トークン、E-54 の SC-22 の試験キーなど）を、
// 成果物の走査（`scripts/check-e2e-artifact-secrets.js --issued-file`）が探せるように記録する。
//
// - 発行の直後に `E2E_ISSUED_<種類>=<値>` の 1 行を `E2E_ISSUED_SECRETS_FILE`（runner の一時領域）へ追記し、権限を 0600 にする。
//   trace・録画を切っても、html レポートのエラーメッセージ・attach・step の題に値が紛れ込む経路は残るので、上げる前に探す。
// - 🔴 値はログに出さない・返さない。例外の文言にも入れない。失効済みの値も同じに扱う（走査の側は区別しない）。
// - `E2E_ISSUED_SECRETS_FILE` が無ければ何もしない（手元の実行。成果物を上げない）。e2e-live.yml は必ず与える（scripts.repo.test.js が見る）。
import { appendFileSync, chmodSync } from 'node:fs';
import type { LiveEnv } from './env';

/** 走査の側（`check-e2e-artifact-secrets.js` の ISSUED_NAME）と同じ形。 */
const NAME = /^E2E_ISSUED_[A-Z0-9_]+$/;
/** 走査の側の最短の長さ（偶然の一致と見分けられない短さの値は記録できない）。 */
const MIN_LENGTH = 12;

export function recordIssuedSecret(
  cfg: Pick<LiveEnv, 'issuedSecretsFile'>,
  name: string,
  value: string,
): void {
  if (!NAME.test(name)) throw new Error(`発行値の名前は E2E_ISSUED_<種類>: ${name}`);
  if (typeof value !== 'string' || value.length < MIN_LENGTH || /[\r\n]/.test(value)) {
    throw new Error(`${name}: 記録できない値（${MIN_LENGTH} 文字以上・改行なし。値は出さない）`);
  }
  if (!cfg.issuedSecretsFile) return;
  appendFileSync(cfg.issuedSecretsFile, `${name}=${value}\n`, { mode: 0o600 });
  chmodSync(cfg.issuedSecretsFile, 0o600);
}
