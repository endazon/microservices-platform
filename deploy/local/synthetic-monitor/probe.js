// NFR-02, NFR-21, ADR-0044, ADR-0076 決定 3・4, IADR-0378 (#1203):
// **合成監視（synthetic）のプローブ。** 低頻度経路（/analysis/ask 系）へ一定間隔で代表リクエストを
// 打ち、SLO の**評価対象そのもの**を存在させる。ADR-0076 決定 3 は「常時トラフィックがある経路」に
// 限って `absent` を併設すると定めており、/analysis/ask 系はここが動いて初めてその条件を満たす。
//
// 🔴 **クラスタ内で完結する。** 外部の監視 SaaS は使わない（計画 08_data-egress-policy）。
// 依存パッケージも持たない（Node 標準の fetch のみ。realm reconcile Job と同じ方針）。
//
// 🔴 **標識は「主体」である。** ヘッダは付けない —— BFF は受信ヘッダを見ず、
// 検証済み JWT の `azp`（＝このクライアント ID）だけで合成と判定する。したがって
// **このプローブが名乗る資格情報そのものが標識**であり、外から偽装できない。
//
// 🔴 **LLM は既定で呼ばれない。** 合成の要求は AiAnalysisService が
// `SyntheticMonitoring:AllowLlmEgress`（既定 false）で縮退させる。ADR-0076 §残るもの が
// 合成監視の**頻度と費用の上限を未定**と残しているため、上限が決まるまで費用を出さない側へ倒す。
// 頻度（PROBE_INTERVAL_SECONDS）も**既定値を実装が決めない**——配備時に必ず与える。

const required = (name) => {
  const value = process.env[name];
  if (!value) {
    console.error(`[synthetic-monitor] 必須の環境変数 ${name} が未設定である。起動しない。`);
    process.exit(1);
  }
  return value;
};

const KC_URL = required('KC_URL');
const REALM = required('KC_REALM');
const CLIENT_ID = required('SYNTHETIC_CLIENT_ID');
const CLIENT_SECRET = required('SYNTHETIC_CLIENT_SECRET');
const BFF_BASE_URL = required('BFF_BASE_URL');
// 🔴 既定値を置かない。ADR-0076 §残るもの が頻度を未定と残しており、実装が数字を決めない。
const INTERVAL_SECONDS = Number(required('PROBE_INTERVAL_SECONDS'));
// 叩く経路（カンマ区切り）。既定は費用の出ない 2 経路のみ。
const PROBE_PATHS = (process.env.PROBE_PATHS || '/bff/analysis/ask,/bff/analysis/ask/stream')
  .split(',').map((p) => p.trim()).filter(Boolean);
// 合成の質問文。**利用者の自由文ではない**ので固定でよい（検索傾向へは入らない＝除外済み）。
const PROBE_QUESTION = process.env.PROBE_QUESTION || 'synthetic monitoring probe';

if (!Number.isFinite(INTERVAL_SECONDS) || INTERVAL_SECONDS <= 0) {
  console.error('[synthetic-monitor] PROBE_INTERVAL_SECONDS は正の数でなければならない。');
  process.exit(1);
}

let cachedToken = null;
let tokenExpiresAt = 0;

async function getToken() {
  const now = Date.now();
  // 期限の 30 秒手前で取り直す（境界で 401 を出さない）。
  if (cachedToken && now < tokenExpiresAt - 30_000) return cachedToken;

  const body = new URLSearchParams({
    grant_type: 'client_credentials',
    client_id: CLIENT_ID,
    client_secret: CLIENT_SECRET,
  });
  const res = await fetch(`${KC_URL}/realms/${REALM}/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body,
  });
  if (!res.ok) throw new Error(`token endpoint returned ${res.status}`);
  const json = await res.json();
  cachedToken = json.access_token;
  tokenExpiresAt = now + (json.expires_in || 60) * 1000;
  return cachedToken;
}

async function probe(path, token) {
  const started = Date.now();
  const res = await fetch(`${BFF_BASE_URL}${path}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${token}` },
    body: JSON.stringify({ question: PROBE_QUESTION }),
  });
  // SSE は本文を読み切ってから閉じる（読まずに閉じるとサーバ側が中断として数える）。
  await res.text();
  const ms = Date.now() - started;
  // 🔴 **応答内容は記録しない。** 合成の応答には権限内の文書名が載り得るため、
  // 経路・状態・所要時間だけを出す（ADR-0006 §結果「ログに本文・機密情報を出力しない」）。
  console.log(`[synthetic-monitor] ${path} status=${res.status} elapsedMs=${ms}`);
}

async function tick() {
  try {
    const token = await getToken();
    for (const path of PROBE_PATHS) {
      try {
        await probe(path, token);
      } catch (err) {
        // 個々の経路の失敗でループを止めない（止めると「無風」に戻り、決定 3 の前提が崩れる）。
        console.error(`[synthetic-monitor] ${path} failed: ${err.message}`);
      }
    }
  } catch (err) {
    // トークンの取得失敗も同様。**失敗しても回り続ける**ことが器の目的である。
    cachedToken = null;
    console.error(`[synthetic-monitor] token acquisition failed: ${err.message}`);
  }
}

console.log(
  `[synthetic-monitor] 起動: interval=${INTERVAL_SECONDS}s paths=${PROBE_PATHS.join(' ')} `
  + `clientId=${CLIENT_ID}（この主体が標識である。ヘッダは付けない）`);

const run = async () => {
  for (;;) {
    await tick();
    await new Promise((resolve) => setTimeout(resolve, INTERVAL_SECONDS * 1000));
  }
};

run();
