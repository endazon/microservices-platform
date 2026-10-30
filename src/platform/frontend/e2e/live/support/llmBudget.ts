// SC-01, SC-08, NFR / IADR-0665（#2035 / E-70）: llm 層（L3-L）の **LLM 呼び出しの上限**の単一情報源。
// spec（`rag.llm.spec.ts`）と L1 の門（`live-opt-in.smoke.spec.ts`）が同じ定数を読む。
//
// - 上限は基準書 §6.2: **1 試験 1 回 × 試験 2 件 × retries 0 = 1 実行 2 回**（週 1 回まで）。費用の自動統制は MSP 側に無いので
//   （Alertmanager の配備までは月次の手動確認）、e2e が自分で上限を持つ。
// - 数えるのはブラウザが送る `POST /bff/search` と `POST /bff/analysis/*`（網羅表の「`POST /search` `POST /analysis*`」）。
//   回答・分析の口と、後段で再順位付けに LLM を使い得る検索の口である。
// - 🔴 **数えるだけでなく止める。** 上限を超えた要求は `page.route` で `abort` し、後段へ届けない（超過が費用を出さない）。
//   止めた要求は記録し、試験の終わりに fail させる。
import { expect, type Page } from '@playwright/test';

/** 1 試験で許す呼び出し（基準書 §6.2）。 */
export const LLM_CALLS_PER_TEST = 1;
/** 1 実行で許す呼び出し（基準書 §6.2。`e2e-live.yml` の `layer` の説明と同じ値）。 */
export const LLM_CALLS_PER_RUN = 2;

/** 数える口か（method と BFF のパス名）。`/bff/search-trends` のような前方一致だけの別の口は数えない。 */
export function isLlmCall(method: string, pathname: string): boolean {
  if (method.toUpperCase() !== 'POST') return false;
  return pathname === '/bff/search' || pathname.startsWith('/bff/analysis/');
}

/** 上限つきの数え手（純粋。`page` を持たない —— L1 が直接試す）。 */
export function createLlmBudget(limit: number) {
  if (!Number.isInteger(limit) || limit < 1) {
    throw new Error(`LLM 呼び出しの上限は 1 以上の整数: ${limit}`);
  }
  const admitted: string[] = [];
  const refused: string[] = [];
  return {
    /** 上限の内なら記録して true、超えれば refused に積んで false。 */
    admit(call: string): boolean {
      if (admitted.length < limit) {
        admitted.push(call);
        return true;
      }
      refused.push(call);
      return false;
    },
    admitted: () => [...admitted],
    refused: () => [...refused],
  };
}

/**
 * `page` の LLM の口に上限を掛ける。上限の内は `continue`、超えたら `abort`（後段へ届けない）。
 * 戻り値の `assertWithin()` を試験の終わりに呼ぶ（ちょうど `expected` 回で、止めた要求が無いこと）。
 */
export async function guardLlmCalls(page: Page, limit = LLM_CALLS_PER_TEST) {
  const budget = createLlmBudget(limit);
  await page.route(
    (url) => url.pathname === '/bff/search' || url.pathname.startsWith('/bff/analysis/'),
    async (route) => {
      const request = route.request();
      const pathname = new URL(request.url()).pathname;
      if (!isLlmCall(request.method(), pathname)) return route.continue();
      return budget.admit(`${request.method()} ${pathname}`)
        ? route.continue()
        : route.abort('blockedbyclient');
    },
  );
  return {
    admitted: budget.admitted,
    assertWithin(expected: number) {
      expect(budget.refused(), '上限を超えて止めた LLM の呼び出し').toEqual([]);
      expect(budget.admitted(), 'LLM の呼び出し').toHaveLength(expected);
    },
  };
}
