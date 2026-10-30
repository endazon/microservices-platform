// NFR / IADR-0569 決定 3 / IADR-0630 決定 4 / IADR-0632（#2033）: live の **project の組み立て**（`playwright.live.config.ts` が使う）。
// 層ごとの project と、その間の依存（順序）をここに置く。L1（`e2e/live-opt-in.smoke.spec.ts`）がこの関数を直接呼んで依存を固定する。
import type { PlaywrightTestConfig } from '@playwright/test';
import type { Layer, LiveEnv } from './env';

type Project = NonNullable<PlaywrightTestConfig['projects']>[number];

/**
 * 使い捨てのスタックでしか流せない試験（#2040 / IADR-0662。M-005 の初回の TOTP 登録。一時アカウントを Keycloak の管理 API で作って消す）。
 * `E2E_DISPOSABLE=1` が無ければ live-ro から外す（常設の PoC では 0 件）。
 */
const DISPOSABLE_ONLY_SPEC = /totp-enroll\.ro\.spec\.ts$/;

/** e2e-revocable を無効化して全セッションを失効させる試験（#2033 / IADR-0632）。 */
const REVOCATION_SPEC = /revocation\.rw\.spec\.ts$/;

/** 層の project（IADR-0569 決定 3 の表）。`live-setup` に依存する。 */
const LAYER_PROJECTS: Record<Layer, Project> = {
  ro: {
    name: 'live-ro',
    testMatch: /\.ro\.spec\.ts$/,
    workers: 2,
    retries: 1,
    dependencies: ['live-setup'],
  },
  rw: {
    name: 'live-rw',
    testMatch: /\.rw\.spec\.ts$/,
    // IADR-0632: e2e-revocable を失効させる試験は live-rw-revocation が最後に流す（ここでは流さない）。
    testIgnore: REVOCATION_SPEC,
    workers: 1,
    retries: 1,
    dependencies: ['live-setup'],
  },
  trading: {
    name: 'trading',
    testMatch: /\.trading\.spec\.ts$/,
    workers: 1,
    retries: 0,
    use: { trace: 'on' },
    dependencies: ['live-setup'],
  },
  llm: {
    name: 'llm',
    testMatch: /\.llm\.spec\.ts$/,
    workers: 1,
    retries: 0,
    dependencies: ['live-setup'],
  },
};

/**
 * live の project（純関数。#2033 / IADR-0632 で config から分けた —— L1 が依存の順序を直接見る）。`E2E_LIVE` が無ければ空。
 */
export function liveProjects(
  cfg: Pick<LiveEnv, 'live' | 'layers'> & Partial<Pick<LiveEnv, 'disposable'>>,
): Project[] {
  // IADR-0662: E2E_DISPOSABLE=1 が無ければ、使い捨てのスタック専用の試験を live-ro から外す。
  const layerProject = (layer: Layer): Project =>
    layer === 'ro' && !cfg.disposable
      ? { ...LAYER_PROJECTS.ro, testIgnore: DISPOSABLE_ONLY_SPEC }
      : LAYER_PROJECTS[layer];
  return cfg.live
    ? [
        {
          name: 'live-setup',
          testMatch: /\.setup\.ts$/,
          workers: 1,
          retries: 0,
          // 🔴 setup は trace・スクリーンショット・動画を残さない。trace は `fill` の値（パスワード）と応答の
          // `Set-Cookie`（セッション cookie）を記録する。失敗した段は spec が文字で attach する（IADR-0624 決定 3）。
          use: { trace: 'off', screenshot: 'off', video: 'off' },
        },
        // IADR-0630 決定 4: rw は同じ実行で ro を選んでいれば ro の後に流す（到達の試験と書き込みを並べない）。
        ...cfg.layers.map((layer) =>
          layer === 'rw' && cfg.layers.includes('ro')
            ? { ...LAYER_PROJECTS.rw, dependencies: ['live-setup', 'live-ro'] }
            : layerProject(layer),
        ),
        // 🔴 #2033 / IADR-0632: e2e-revocable を無効化する試験は、その役割の状態を使う他の試験（live-ro の auth-errors・live-rw の
        // E-54 のリセット申請・アカウントコンソールなど）が**すべて終わってから**流す。project の依存で順序を固定する（同じ実行で選んだ層の project 全部に依存する）。
        // 無効化はその利用者の全セッションを失効させ、再有効化してもセッションは戻らない。
        ...(cfg.layers.includes('rw')
          ? [
              {
                name: 'live-rw-revocation',
                testMatch: REVOCATION_SPEC,
                workers: 1,
                retries: 0,
                dependencies: [
                  'live-setup',
                  ...cfg.layers
                    .filter((l) => l === 'ro' || l === 'rw')
                    .map((l) => LAYER_PROJECTS[l].name!),
                ],
              } satisfies Project,
            ]
          : []),
      ]
    : [];
}
