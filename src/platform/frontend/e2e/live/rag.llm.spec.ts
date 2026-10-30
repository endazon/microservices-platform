// SC-01 / SC-08 / FR-04 / FR-07 / NFR / IADR-0569 / IADR-0627 / IADR-0665（#2035 / E-70）: **llm 層（L3-L）**。網羅表（#2013）の
// M-031（SC-01 の質問 1 件）と M-052（SC-08 の分析 1 件）を、`live-setup` が作った `e2e-user` の storageState で流す。
//
// - 🔴 **1 実行の LLM 呼び出しは 2 以下**（1 試験 1 回 × 2 件 × retries 0。基準書 §6.2）。`support/llmBudget.ts` が `page.route` で
//   `POST /bff/search` と `POST /bff/analysis/*` を数え、上限を超えた要求は**後段へ届けずに止めて** fail させる。件数と retries は
//   L1（`live-opt-in.smoke.spec.ts`）が `--list` で固定する。流すのは週 1 回まで（`e2e-live.yml` の dispatch の `layer`）。
// - **索引なしは skip**（`E2E_SEARCH_HITS=1` が無ければ 2 件とも理由つきで skip し、呼び出しは 0）。
// - **索引の待ちに LLM の口を使わない。** 対象範囲の候補（`POST /bff/attribute-values`。索引から引き、LLM を呼ばない）に seed 文書の
//   部門が出るまで画面を開き直して待つ。`POST /bff/search` で待つと、待つたびに上限を使う。
// - **本文の中身は見ない。** 使い捨てのスタック（`LLM_KEYLESS=1`）ではゲートウェイに鍵が無く、後段は出典つきの縮退の文を返す。
//   判定は「1 回だけ送った・200・出典に seed 文書・失敗の表示が無い」に揃え、実の回答と縮退の文のどちらでも通す（IADR-0665 決定 4）。
// - 質問・依頼の文は短く固定し、`e2e-<runId>` を前に付ける（基準書 §3 の識別子。出力のトークンを抑える）。
import { readFileSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { parseLiveEnv, stateFile } from './support/env';
import { DEFAULT_AUTH_DIR } from './support/paths';
import { ja } from './support/catalog';
import { guardLlmCalls, LLM_CALLS_PER_TEST } from './support/llmBudget';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

interface SeedDoc {
  title: string;
  attributes: Record<string, string>;
}
/** seed の宣言が単一情報源（タイトル・属性をここへ写さない）。 */
const SEED = JSON.parse(
  readFileSync(
    new URL('../../../../../deploy/local/search-seed/documents.json', import.meta.url),
    'utf8',
  ),
) as { probeTerm: string; documents: SeedDoc[] };
const PUBLIC_DOC = SEED.documents.find((d) => d.attributes.confidentiality === 'public');
if (!PUBLIC_DOC) throw new Error('seed に confidentiality=public の文書が無い（IADR-0627）');
const SEED_TITLES = SEED.documents.map((d) => d.title);

/** 画面の文言は製品の ja カタログから引く（msgid が無ければ読み込みで例外＝L1 の `--list` で赤）。 */
const T = {
  askHeading: ja('ナレッジ検索・AI質問'),
  questionLabel: ja('質問・キーワード'),
  send: ja('送信'),
  regenerate: ja('再生成'),
  citationsHeading: ja('出典（クリックで文書詳細／Wikiへ）'),
  askFailed: ja('AI 回答を生成できませんでした。キーワード検索の結果をご確認ください。'),
  analyzeHeading: ja('AI分析依頼'),
  scopeFilter: ja('対象範囲フィルタ'),
  instruction: ja('分析内容（指示）'),
  run: ja('分析実行'),
  result: ja('結果'),
  analyzeFailed: ja('分析を実行できませんでした。'),
  analyzeEmpty: ja('該当する情報が見つかりませんでした。'),
};

const HITS_REASON =
  'E2E_SEARCH_HITS=1 が無い（索引が空であり得る環境では、LLM を呼ぶ試験を流さない。呼び出しは 0。IADR-0627 / IADR-0665）';
/** 取り込みを待つ上限（seed は up の中と readiness の後に投入され、取り込みは非同期）。 */
const INDEX_WAIT = { timeout: 120_000, intervals: [3_000, 5_000, 10_000] };
/** 回答・分析を待つ上限（LLM の応答。縮退なら速い）。 */
const ANSWER_TIMEOUT = 120_000;
const SLOW_TEST_MS = 300_000;

const runTag = `e2e-${cfg.runId ?? 'local'}`;
const isBff = (url: string, path: string) => new URL(url).pathname === path;

/**
 * 画面を開き直しながら、対象範囲の候補に seed の public 文書の部門が出るまで待つ（索引に入った印）。
 * 候補は索引から引き、LLM を呼ばない。見つけた候補のボタンを返す。
 */
async function waitForSeedCandidate(page: Page, path: string, heading: string) {
  const chip = page
    .getByRole('region', { name: T.scopeFilter })
    .getByRole('button', { name: PUBLIC_DOC!.attributes.department, exact: true });
  await expect(async () => {
    await page.goto(path);
    await expect(page.getByRole('heading', { name: heading, level: 1 })).toBeVisible();
    await expect(chip).toBeVisible({ timeout: 10_000 });
  }).toPass(INDEX_WAIT);
  return chip;
}

test.describe('e2e-user の LLM 呼び出し（llm 層）', () => {
  test.use({ storageState: stateFile(cfg.authDir, 'user') });

  // SC-01, FR-04: M-031。
  test(
    'M-031: 固定の短い質問 1 件を送ると、seed 文書を出典に持つ回答が完了する（LLM 1 回）',
    { tag: ['@p1', '@llm'] },
    async ({ page }) => {
      test.skip(!cfg.searchHits, HITS_REASON);
      test.setTimeout(SLOW_TEST_MS);
      const llm = await guardLlmCalls(page);
      await waitForSeedCandidate(page, '/ask', T.askHeading);

      const responded = page.waitForResponse(
        (r) => isBff(r.url(), '/bff/analysis/ask/stream') && r.request().method() === 'POST',
      );
      await page
        .getByLabel(T.questionLabel)
        .fill(`${runTag} ${SEED.probeTerm} の要点を 1 文で答えて`);
      await page.getByRole('button', { name: T.send, exact: true }).click();
      expect((await responded).status(), 'POST /bff/analysis/ask/stream').toBe(200);

      // 完了の印: ストリームが終わると「再生成」が出る。
      await expect(page.getByRole('button', { name: T.regenerate })).toBeVisible({
        timeout: ANSWER_TIMEOUT,
      });
      await expect(page.getByText(T.askFailed)).toHaveCount(0);
      // 出典に seed 文書が在る（実の回答でも縮退の文でも出典は付く）。
      await expect(page.getByRole('heading', { name: T.citationsHeading })).toBeVisible();
      const seedLinks = SEED_TITLES.map((title) =>
        page.getByRole('link', { name: title, exact: true }),
      );
      // 同じ文書がチャンクごとに複数の出典として並び得るので、件数で見る（0 でない）。
      await expect(seedLinks.reduce((x, y) => x.or(y))).not.toHaveCount(0);

      llm.assertWithin(LLM_CALLS_PER_TEST);
    },
  );

  // SC-08, FR-07: M-052。
  test(
    'M-052: 対象（部門）1 件と短い依頼で分析を実行すると、seed 文書を出典に持つ結果が出る（LLM 1 回）',
    { tag: ['@p2', '@llm'] },
    async ({ page }) => {
      test.skip(!cfg.searchHits, HITS_REASON);
      test.setTimeout(SLOW_TEST_MS);
      const llm = await guardLlmCalls(page);
      const chip = await waitForSeedCandidate(page, '/analyze', T.analyzeHeading);
      await chip.click();
      await expect(chip).toHaveAttribute('aria-pressed', 'true');

      await page.getByLabel(T.instruction).fill(`${runTag} ${SEED.probeTerm} の要点を 1 文で`);
      const responded = page.waitForResponse(
        (r) => isBff(r.url(), '/bff/analysis/analyze') && r.request().method() === 'POST',
      );
      await page.getByRole('button', { name: T.run, exact: true }).click();
      const res = await responded;
      expect(res.status(), 'POST /bff/analysis/analyze').toBe(200);
      // 依頼に選んだ対象（部門）が載っている。
      expect(JSON.stringify(res.request().postDataJSON())).toContain(
        PUBLIC_DOC!.attributes.department,
      );

      await expect(page.getByRole('heading', { name: T.result, exact: true })).toBeVisible({
        timeout: ANSWER_TIMEOUT,
      });
      await expect(page.getByText(T.analyzeFailed)).toHaveCount(0);
      await expect(page.getByText(T.analyzeEmpty)).toHaveCount(0);
      // 出典に public の seed 文書（選んだ部門の文書）が在る。
      await expect(
        page.getByRole('link', { name: PUBLIC_DOC!.title, exact: true }),
      ).not.toHaveCount(0);

      llm.assertWithin(LLM_CALLS_PER_TEST);
    },
  );
});
