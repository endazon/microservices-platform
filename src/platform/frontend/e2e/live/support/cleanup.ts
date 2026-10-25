// NFR / IADR-0629（#2030 / E-50）: live の書き込み層（L3）の**後始末の共通部品**（基準書 §7・live-poc.md §3）。
//
// - test は作ったものを `track(名前, 手順…)` で積み、afterEach で `run(testInfo)` を呼ぶ。`run` は**積んだ逆順**に、各手順を
//   `/bff/*` へ**直接**送る（画面を経由しない。画面の都合で消せない状態を避ける）。`page.request` を渡せば同じ cookie を使う。
// - 🔴 名前は `e2e-` で始まる（sweep が残りを拾う接頭辞）。経路は `/bff/` で始まる。どちらも**積む時点で例外**にする。
// - 404 は消えたとみなす（test の本文が消した・論理削除の後に完全削除へ進んだ）。それ以外の失敗でも**残りの後始末を続け**、
//   消せなかったものを「未削除一覧」として test に attach する（test は落とさない。残りは sweep が拾う）。
// - 状態変更の要求には `X-MSP-CSRF` を付ける（BFF の 2 枚目の壁。値に意味は無い。SPA の apiClient と同じ）。
import type { APIRequestContext, TestInfo } from '@playwright/test';

const PREFIX = 'e2e-';

/** 後始末の 1 手順（`/bff/*` への状態変更の要求）。 */
interface CleanupStep {
  method: 'DELETE' | 'POST' | 'PUT' | 'PATCH';
  path: string;
  data?: unknown;
}

/** 消せなかったもの（名前・失敗した手順・理由。本文や cookie は持たない）。 */
interface Leftover {
  name: string;
  step: string;
  reason: string;
}

interface Cleanup {
  /** 作ったものを積む。手順は書いた順に送る（例: 論理削除 → 完全削除）。 */
  track(name: string, ...steps: CleanupStep[]): void;
  /** 積んだ逆順に後始末を送り、消せなかったものを attach して返す。積んだものは空にする。 */
  run(testInfo: TestInfo): Promise<Leftover[]>;
}

/**
 * 作るものの名前（`e2e-<runId>-<種類>-r<再試行>`。#2031 / IADR-0630 決定 3）。再試行は前の試行の名前と重ならない。
 * runId は config が実行ごとに 1 回だけ決める（`E2E_RUN_ID`）。
 */
export function e2eName(runId: string | null, kind: string, retry: number): string {
  if (!runId) throw new Error('E2E_RUN_ID が無い（playwright.live.config.ts が実行ごとに決める）');
  if (!/^[a-z0-9]+(-[a-z0-9]+)*$/.test(kind))
    throw new Error(`名前の種類は英小文字・数字・ハイフン: ${kind}`);
  return `${PREFIX}${runId}-${kind}-r${retry}`;
}

export function createCleanup(request: APIRequestContext): Cleanup {
  const entries: { name: string; steps: CleanupStep[] }[] = [];
  return {
    track(name, ...steps) {
      if (!name.startsWith(PREFIX)) {
        throw new Error(`後始末に積む名前は ${PREFIX} で始める（sweep が拾えない）: ${name}`);
      }
      if (steps.length === 0) throw new Error(`後始末の手順が無い: ${name}`);
      for (const s of steps) {
        if (!s.path.startsWith('/bff/'))
          throw new Error(`後始末は /bff/ へ直接送る: ${s.method} ${s.path}`);
      }
      entries.push({ name, steps });
    },
    async run(testInfo) {
      const leftovers: Leftover[] = [];
      for (const entry of entries.splice(0).reverse()) {
        for (const s of entry.steps) {
          const step = `${s.method} ${s.path}`;
          try {
            const res = await request.fetch(s.path, {
              method: s.method,
              data: s.data,
              headers: { 'X-MSP-CSRF': '1' },
              failOnStatusCode: false,
              maxRedirects: 0,
            });
            if (res.status() === 404 || res.ok()) continue;
            leftovers.push({ name: entry.name, step, reason: `${res.status()}` });
          } catch (e) {
            leftovers.push({
              name: entry.name,
              step,
              reason: e instanceof Error ? e.message.split('\n')[0] : String(e),
            });
          }
          break; // この手順で止まったものの後続（完全削除など）は送らない
        }
      }
      if (leftovers.length) {
        await testInfo.attach('未削除一覧', {
          body: JSON.stringify(leftovers, null, 2),
          contentType: 'application/json',
        });
        testInfo.annotations.push({
          type: '未削除',
          description: leftovers.map((l) => l.name).join(', '),
        });
      }
      return leftovers;
    },
  };
}
