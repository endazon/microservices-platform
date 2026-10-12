#!/usr/bin/env node
'use strict';
/*
 * check-ci-latency.js
 * `build-and-test` の待ち時間が**自分自身の過去の床**から伸びたことを検知する
 * （IADR-0232 決定 8。2026-10-12 追記 / #1910 で判定方式を置き換えた）。
 *
 * 背景:
 *   PR の待ち時間は必須チェックの最大値で決まる。ビルド時間は機能追加とともに連続的に伸びるため、
 *   **いつ伸びたか誰も気付かない**。これは自動起票や fail-closed の門と同じ「**気付けない種類の劣化**」であり、
 *   検知を機械に任せる。
 *
 *   旧方式（2026-08-22〜2026-10-12）は「監視対象の中央値 > `claude-review` の**最小値**」で鳴らした。
 *   前提は「必須チェックの最長は当面 claude-review で、その下限は動かない」だった。
 *   2026-10-10 にレビューのモデルを切り替えて claude-review の下限が約 70 秒で底を打ち
 *   （準備 35〜50 秒＋処理 16〜60 秒）、`build-and-test` の中央値（約 156 秒）が恒常的に上回った。
 *   **前提が失われたので、基準を「レビューの下限」から「監視対象自身の過去の床」へ改めた**（利用者裁定 2026-10-12）。
 *   claude-review との比較は**参考値**として出し続ける（判定には使わない）。
 *
 * 方式:
 *   マージ済み PR を新しくマージされた順に並べ、N 本（`CI_LATENCY_SAMPLES`）ずつ窓に区切る。
 *   先頭の窓が「現在」、その後ろの K 個が「過去」。
 *     現在の窓の監視対象の**中央値**  >  過去 K 窓それぞれの中央値の**最小値**（床） × R
 *   なら exit 1。
 *
 *   🔴 **現在の窓だけを epoch で絞る。過去の窓は絞らない**（IADR-0241。2026-10-12 追記）。
 *   `ci.yml` はほぼ毎日変わる（8/20 以降の 53 日で 52 回）。過去の窓まで絞ると床を作れる日が無くなり、
 *   監視は恒常的に skip する。床は「この check がどこまで速く走れたか」の記録であり、構成をまたいでよい。
 *   **レビュー側の構成変更（`claude-code-review.yml`）は epoch に含めない** —— 判定はもう claude-review を使わない。
 *
 *   🔴 **check-run 自身の所要（completed_at - started_at）で測らない。** 実データで踏んだ罠である ——
 *   `build-and-test` は matrix の脚を `needs:` で束ねる**集約ジョブ**であり、
 *   自身の所要は **15 秒**しかない（実測。#894 の head）。脚の 111 秒はそこに現れない。
 *   待ち時間として意味を持つのは **check 群の開始からの完了オフセット（同 126 秒）**である。
 *   所要で測ると、集約ジョブは実際の 1/8 に見え、**監視が永久に鳴らない**。
 *
 *   🔴 **しきい値を固定値で持たない（自己校正）。** 床も同じ check の過去の run から実測する。
 *   固定値は環境とコードベースの変化で必ず腐る（本作業でも「キャッシュは初回必ずミス」という
 *   固定の思い込みが実測で誤りだった）。R は秒数ではなく倍率である。
 *
 *   🔴 **監視対象は中央値、基準は最小値。** 対象を中央値にするのは 1 本の外れ値（ランナーの
 *   当たり外れ）で鳴らさないため。基準を（過去の窓の中央値の）最小値にするのは、守るべき相手が
 *   「この check が出せていた速さ」の下限だからである。**この非対称は意図的であり、揃えてはならない。**
 *
 *   🔴 **限界: K 窓より遅い速度で伸び続ける劣化は、床が一緒に上がるので鳴らない。** 床の比較は
 *   「急な段差」の検知であり、長期の漂流の検知ではない。漂流は参考値を週次の出力で人が見る
 *   （既定値の選定と実測は作業仕様書 20261012_1910_ci-latency-self-floor.md）。
 *
 * 使い方:
 *   GITHUB_REPOSITORY=owner/repo GITHUB_TOKEN=... node scripts/check-ci-latency.js
 *   node scripts/check-ci-latency.js --self-test
 *   node scripts/check-ci-latency.js --report-only   # 判定せず報告のみ（exit 0）
 *
 * 環境変数:
 *   CI_LATENCY_TARGET     監視対象の check 名（既定 build-and-test）
 *   CI_LATENCY_BASELINE   参考値に出す check 名（既定 claude-review）。**判定には使わない**
 *   CI_LATENCY_SAMPLES    1 窓のマージ済み PR の本数 N（既定 20）
 *   CI_LATENCY_FLOOR_WINDOWS  床を取る過去の窓の数 K（既定 3。1 以上の整数）
 *   CI_LATENCY_FLOOR_RATIO    床に対する倍率 R（既定 1.25。1 より大きい数）。現在の中央値が床 × R を超えたら赤
 *   CI_LATENCY_STEP_RATIO 定常性の門の倍率（既定 2）。中央値 ÷ 最小 がこれ以上なら判定を skip する
 *   CI_LATENCY_CONFIG_PATH  CI 構成ファイル（既定 .github/workflows/ci.yml）。epoch の導出に使う
 *   CI_LATENCY_EPOCH        epoch の上書き（ISO 8601）。**両方向の実測に使う**
 *   CI_LATENCY_MIN_SAMPLES  現在の窓の判定に要る最小本数（既定 3。1 以上の整数）
 *   CI_LATENCY_FLOOR_MIN_SAMPLES  過去の窓が床の候補になる最小本数（既定 ceil(N / 2)＝N の半分以上。1 以上の整数）
 */

const TARGET = process.env.CI_LATENCY_TARGET || 'build-and-test';
const BASELINE = process.env.CI_LATENCY_BASELINE || 'claude-review';
// 🔴 既定 20（#1910 で 10 から上げた）。10 本の窓の中央値はランナー差だけで ±15〜18% 揺れ、R（1.25）の余裕を食う。
const SAMPLES = Number(process.env.CI_LATENCY_SAMPLES || 20);
// 既定値の根拠（8/20〜10/12 の 572 本で再計算した安定期の誤報率・悪化期の検知率）は作業仕様書にある。
const FLOOR_WINDOWS = Number(process.env.CI_LATENCY_FLOOR_WINDOWS || 3);
const FLOOR_RATIO = Number(process.env.CI_LATENCY_FLOOR_RATIO || 1.25);
const STEP_RATIO = Number(process.env.CI_LATENCY_STEP_RATIO || 2);
const CONFIG_PATH = process.env.CI_LATENCY_CONFIG_PATH || '.github/workflows/ci.yml';
// 🔴 中央値は本数が少ないと 1 本に引きずられる。3 本を下限とする。
const MIN_SAMPLES = Number(process.env.CI_LATENCY_MIN_SAMPLES || 3);
// 🔴 過去の窓が床の候補になる本数は **N に連動させる**（既定 ceil(N / 2)。#1912 のレビュー指摘）。
// 現在の窓と同じ 3 本で候補にすると、たまたま速かった少数の窓が K 窓のあいだ床を固定し、誤報側へ倒れる。
// 過去の窓は epoch で絞らないので本数が欠けるのは API 失敗か skip された run だけであり、半分を要求しても履歴の門はまず効かない。
const FLOOR_MIN_SAMPLES = Number(process.env.CI_LATENCY_FLOOR_MIN_SAMPLES || Math.ceil(SAMPLES / 2));
const FILES_PAGE_SIZE = 100;
// GitHub の `/pulls/{n}/files` は最大 3000 件までしか返さない（＝30 ページ）。
const FILES_MAX_PAGES = 30;

/**
 * 秒。completed_at - started_at。**実際に走った run だけ**を返し、それ以外は null。
 *
 * 🔴 **skip / cancelled / action_required を必ず落とす。** 実データで踏んだ罠である ——
 * これらは started_at == completed_at で **0 秒**として返り、
 *   - 基準（claude-review）側に混ざると**最小が 0 秒**になり、監視が**常に鳴る**
 *   - 監視対象側に混ざると中央値を押し下げ、監視が**常に鳴らない**
 * どちらも「監視が付いている」という記録だけを残して機能しない。
 * **0 秒を弾くだけでは足りない**（1〜2 秒で終わる skip 相当もあるため conclusion で絞る）。
 */
function durationSec(run) {
  if (!run || !run.started_at || !run.completed_at) return null;
  if (run.status !== 'completed') return null;
  if (run.conclusion !== 'success' && run.conclusion !== 'failure') return null;
  const d = (Date.parse(run.completed_at) - Date.parse(run.started_at)) / 1000;
  if (!Number.isFinite(d) || d <= 0) return null;
  return d;
}

/**
 * その head の check 群が動き出した時刻（ミリ秒）。**skip された run も含めて**最小の
 * `started_at` を取る —— 起動しなかった run も「起動判定はされた」時刻を持つため、
 * 群の開始点としては使える。1 本も無ければ null。
 */
function checkSetStart(runs) {
  const ts = (runs || [])
    .map((r) => (r && r.started_at ? Date.parse(r.started_at) : NaN))
    .filter((n) => Number.isFinite(n));
  return ts.length ? Math.min(...ts) : null;
}

/**
 * 秒。**check 群の開始からの完了オフセット**を返す。これが利用者の待ち時間である。
 * 実際に走った run（`durationSec` が値を返す run）だけを対象にする。
 */
function waitSec(run, t0) {
  if (t0 === null || durationSec(run) === null) return null;
  const w = (Date.parse(run.completed_at) - t0) / 1000;
  if (!Number.isFinite(w) || w <= 0) return null;
  return w;
}

/** 中央値。空配列は null。偶数個は下側（保守的に小さく見る＝鳴りにくい側）。 */
function median(xs) {
  if (!xs.length) return null;
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.floor((s.length - 1) / 2)];
}

function min(xs) {
  return xs.length ? Math.min(...xs) : null;
}

/**
 * 設定値（K・R・N）の検査。不正なら理由の文字列、正しければ null。
 * 🔴 **不正値を黙って既定へ戻さない。** `CI_LATENCY_FLOOR_RATIO=1` は「床から 1 秒でも伸びたら鳴る」、
 * `0` や数値でない値は `NaN` 比較で**常に緑**になる。どちらも監視が黙って壊れる形なので、設定の誤りとして赤くする。
 */
function validateConfig({
  floorWindows = FLOOR_WINDOWS,
  floorRatio = FLOOR_RATIO,
  samples = SAMPLES,
  minSamples = MIN_SAMPLES,
  floorMinSamples = FLOOR_MIN_SAMPLES,
  stepRatio = STEP_RATIO,
} = {}) {
  if (!Number.isInteger(floorWindows) || floorWindows < 1) {
    return `CI_LATENCY_FLOOR_WINDOWS は 1 以上の整数でなければならない（現在 ${floorWindows}）`;
  }
  if (!Number.isFinite(floorRatio) || floorRatio <= 1) {
    return `CI_LATENCY_FLOOR_RATIO は 1 より大きい数でなければならない（現在 ${floorRatio}）`;
  }
  if (!Number.isInteger(samples) || samples < 1) {
    return `CI_LATENCY_SAMPLES は 1 以上の整数でなければならない（現在 ${samples}）`;
  }
  // 🔴 NaN だと `length < NaN` も `t >= lo * NaN` も false になり、門を**素通り**する（#1912 のレビュー指摘）。
  if (!Number.isInteger(minSamples) || minSamples < 1) {
    return `CI_LATENCY_MIN_SAMPLES は 1 以上の整数でなければならない（現在 ${minSamples}）`;
  }
  if (!Number.isInteger(floorMinSamples) || floorMinSamples < 1) {
    return `CI_LATENCY_FLOOR_MIN_SAMPLES は 1 以上の整数でなければならない（現在 ${floorMinSamples}）`;
  }
  if (!Number.isFinite(stepRatio) || stepRatio <= 1) {
    return `CI_LATENCY_STEP_RATIO は 1 より大きい数でなければならない（現在 ${stepRatio}）`;
  }
  return null;
}

/**
 * 床＝過去の窓それぞれの中央値の**最小値**。
 * 監視対象の値が `minSamples` 本に満たない窓は候補にしない（少数の中央値は外れ値そのものになる）。
 * 呼び出し側は `FLOOR_MIN_SAMPLES`（既定 ceil(N / 2)）を渡す。
 * 返す `windows` は候補になった窓の数（履歴の門が使う）。
 *
 * 🔴 **「全値の最小」でも「中央値の中央値」でもない。** 全値の最小は 1 本の当たりの速い run で床が下がり
 * 常に鳴る。中央値の中央値は過去の悪化を床へ取り込み、段差を見逃す。窓ごとの中央値で外れ値を消してから、
 * その中で最も速かった窓を床にする。
 */
function floorOf(pastWindows, { minSamples = MIN_SAMPLES } = {}) {
  const medians = (pastWindows || [])
    .filter((w) => Array.isArray(w) && w.length >= minSamples)
    .map((w) => median(w));
  return { floor: min(medians), windows: medians.length, medians };
}

/**
 * 判定。値が揃わないときは **fail-open**（exit 0）にする。
 * 🔴 理由: 本検査は「速さ」の監視であって正しさの門ではない。API が引けない・
 * サンプルが足りないといった理由で赤くすると、**本来の失敗と区別が付かない雑音**になる。
 * ただし **skip したことは必ず出力する**（黙って 0 件検査で緑を返さない）。
 *
 * 🔴 **定常性の門（実データで踏んだ罠）。** 中央値は「直近 N 本が同じ CI 構成で走った」と
 * 仮定している。CI を作り変えた直後はこの仮定が崩れ、**中央値は消えたはずの旧構成を指す**
 * ——実測（MSP・2026-08-21）では最適化直後のサンプル 12 本のうち **11 本が最適化前**で、
 * 中央値 447 秒に対し現在の構成は 126 秒だった。**このまま鳴らせば、直したばかりの CI を
 * 「遅い」と起票する。** 監視が最初の 1 回で狼少年になると、以後の本物も無視される。
 *
 * そこで**監視対象サンプルの中央値が最小の `stepRatio` 倍（既定 2）以上なら判定しない**。
 * ランナーの当たり外れは実測で 1.2 倍程度に収まるため、2 倍は構成変更だけを拾う。
 *
 * **代償を明記する: 窓の途中で実際にビルドが 2 倍に伸びた場合も、この門は 1 週遅らせる**
 * （窓が入れ替われば次回鳴る）。速さの監視は fail-open であるという上の方針と同じ向きであり、
 * **見逃しより誤報のほうが監視を壊す**という判断で意図的にこちらへ倒している。
 *
 * 🔴 **履歴の門（#1910）。** 床の候補になる過去の窓が `floorWindows` 個に満たなければ判定しない。
 * 少ない窓の最小は床として浅く（揺れた 1 窓がそのまま床になる）、判定の意味が設定値と食い違う。
 *
 * 判定（#1910）: 現在の窓の中央値 > 床 × `floorRatio` なら NG。**ちょうど R 倍は緑**（超えたら赤）。
 * `baselineDurations`（claude-review）は**参考値**（`baseline` / `reversed`）としてだけ返し、判定には使わない。
 */
function judge({
  targetDurations,
  baselineDurations = [],
  pastWindows = [],
  floorWindows = FLOOR_WINDOWS,
  floorRatio = FLOOR_RATIO,
  stepRatio = STEP_RATIO,
  minSamples = MIN_SAMPLES,
  floorMinSamples = FLOOR_MIN_SAMPLES,
  epoch = null,
}) {
  const t = median(targetDurations);
  const b = min(baselineDurations);
  const { floor, windows } = floorOf(pastWindows, { minSamples: floorMinSamples });
  // 参考値（判定には使わない）。旧方式の「逆転」——監視対象の中央値が claude-review の下限を超えたか。
  const ref = { target: t, baseline: b, reversed: t !== null && b !== null ? t > b : null, floor, windows };
  // 🔴 **現在の CI 構成で測られた PR が少なすぎるうちは判定しない**（IADR-0241）。
  // 🔴 **この門を null 判定より前に置く。** 0 本のときこそ epoch を理由に添えねばならない ——
  // 「サンプルが足りない」とだけ言われると、**check 名の設定ミスと区別が付かない**。
  // 中央値は「窓の多数派」を映す。構成を変えた直後は多数派が旧構成のままなので、
  // **判定すれば必ず旧構成の値を現在の値として報告する**。決定 8 の定常性の門は
  // 段差が 2 倍未満だとくぐられる（実測 223 → 125 秒 ＝ 1.8 倍）ため、これだけでは足りない。
  if (targetDurations.length < minSamples) {
    return {
      ok: true,
      skipped: true,
      reason:
        `現在の CI 構成（${epoch ? `${epoch} 以降` : 'epoch 不明'}）で測られた ${TARGET} の対象が ` +
        `${targetDurations.length} 本しかない（判定には ${minSamples} 本要る）。` +
        '中央値がまだ現在の構成を表さないため判定しない',
      ...ref,
    };
  }
  if (t === null) {
    return { ok: true, skipped: true, reason: 'サンプルが足りない', ...ref };
  }
  // 🔴 定常性の門。上の説明を参照。
  const lo = min(targetDurations);
  if (lo !== null && t >= lo * stepRatio) {
    return {
      ok: true,
      skipped: true,
      reason: `監視対象のサンプルが CI 構成の変更をまたいでいる（最小 ${Math.round(lo)} 秒 / 中央値 ${Math.round(t)} 秒 = ${(t / lo).toFixed(1)} 倍 ≧ ${stepRatio} 倍）。中央値が現在の構成を表していないため判定しない`,
      ...ref,
    };
  }
  // 🔴 履歴の門（#1910）。黙って緑にしない —— skipped を立て、何個あって何個要るかを出す。
  if (windows < floorWindows || floor === null) {
    return {
      ok: true,
      skipped: true,
      reason:
        `床を取る過去の窓が足りない（${floorMinSamples} 本以上の値を持つ過去の窓が ${windows} 個。判定には ${floorWindows} 個要る）。` +
        '床が決まらないため判定しない',
      ...ref,
    };
  }
  return { ok: t <= floor * floorRatio, skipped: false, ratio: t / floor, ...ref };
}

/**
 * マージ順（新しい順）に並んだ PR を、現在の窓（先頭 `samples` 本）と過去の窓（続く `samples` 本ずつ × `floorWindows`）に区切る。
 * 🔴 **窓の境界はマージ順だけで決める。** 現在の窓で epoch により外した PR を過去の窓へ繰り下げない ——
 * 繰り下げると、epoch が動くたびに過去の窓の中身がずれ、床が構成変更の頻度に依存して揺れる。
 * 過去の窓が `samples` 本に満たない（PR が足りない）ときは、その窓は短いまま返す（候補になるかは `floorOf` が本数で決める）。
 */
function splitWindows(prs, { samples = SAMPLES, floorWindows = FLOOR_WINDOWS } = {}) {
  const list = prs || [];
  const current = list.slice(0, samples);
  const past = [];
  for (let k = 1; k <= floorWindows; k += 1) {
    const w = list.slice(k * samples, (k + 1) * samples);
    if (!w.length) break;
    past.push(w);
  }
  return { current, past };
}

async function fetchJson(url, token) {
  const res = await fetch(url, {
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    },
  });
  if (!res.ok) {
    const err = new Error(`${res.status} ${res.statusText} for ${url}`);
    err.status = res.status;
    err.rateLimited = isRateLimited(res.headers);
    throw err;
  }
  return res.json();
}

/**
 * レート制限による失敗か。**GitHub はレート制限超過にも 403 を返す**ため、
 * ステータスだけ見ると権限誤りと区別が付かない（AI レビューの指摘）。
 * レート制限は待てば直る＝**一過性**なので、赤くしてはならない。
 */
function isRateLimited(headers) {
  if (!headers || typeof headers.get !== 'function') return false;
  return headers.get('x-ratelimit-remaining') === '0' || Boolean(headers.get('retry-after'));
}

/**
 * 権限・認証・存在の誤り（＝**直さない限り永久に直らない**失敗）か。
 *
 * 🔴 **これを 5xx やネットワーク断と同じ「fail-open」で扱ってはならない。** 本検査器は
 * 速さの監視なので API が引けないときは緑を返す設計だが、**権限設定を間違えていると
 * 毎回同じ 403 で早期終了し、監視は一度も働かないまま緑を返し続ける**。
 * それは本 ADR が「監視が黙って壊れるのは監視が無いより悪い」と書いている事態そのものである。
 *
 * 実際、ワークフローの `permissions` は当初 `actions: read` だけだった。スクリプトが叩くのは
 * **Pulls API（`pull-requests`）と Checks API（`checks`）**であり、別スコープである
 * （AI レビューが両リポジトリで独立に指摘した）。**その形はこの分岐が無ければ緑のまま素通りする。**
 */
function classifyFailure(status, { rateLimited = false, scope = 'repo' } = {}) {
  // レート制限（403 で来る）は待てば直る。権限誤りと同じ扱いにしない。
  if (rateLimited) return 'transient';
  if (status === 401 || status === 403) return 'config';
  if (status === 404) {
    // 🔴 **404 の意味は文脈で変わる**（AI レビューの指摘）。
    // リポジトリ／PR 一覧が 404 = リポジトリ名の誤りかアクセス権なし＝**設定の誤り**。
    // 個別コミットの check-runs が 404 = **その 1 本が消えただけ**（スカッシュ後に
    // head ブランチが削除され、dangling commit が GC された等）。
    // ここを一緒くたにすると、**1 本 GC されただけでバッチ全体が赤くなり誤起票する**。
    return scope === 'repo' ? 'config' : 'missing';
  }
  return 'transient'; // 5xx・ネットワーク断
}

/**
 * マージ済み PR を**新しくマージされた順**に並べる。
 *
 * 🔴 **`sort=updated` の順をそのまま使わない。** `updated_at` はマージ後のコメント追加でも
 * 進むため、**古い PR が新しい PR より前に来る**ことがある（AI レビューの指摘）。
 * 監視は「直近の CI 構成」を見たいので、**`merged_at` で並べ直す**。
 */
function sortByMergedAtDesc(prs) {
  return prs
    .filter((p) => p && p.merged_at)
    .sort((a, b) => Date.parse(b.merged_at) - Date.parse(a.merged_at));
}

/**
 * PR の変更ファイル名を**全ページ**集める。
 * `complete` は「最後のページまで読み切れたか」であり、false のときに母集合を狭めてはならない。
 * fetchPage を引数に取るのは、ネットワークを伴わずに自己試験できるようにするためである。
 */
async function fetchChangedFiles(fetchPage, { maxPages = FILES_MAX_PAGES } = {}) {
  const names = [];
  for (let page = 1; page <= maxPages; page += 1) {
    const batch = await fetchPage(page);
    const list = Array.isArray(batch) ? batch : [];
    for (const f of list) {
      if (f && f.filename) names.push(f.filename);
    }
    if (list.length < FILES_PAGE_SIZE) return { names, complete: true };
  }
  return { names, complete: false };
}

/**
 * 母集合の PR が実際にマージされた先＝**統合ブランチ**を、PR 自身から導く。
 *
 * 🔴 **ブランチ名を焼き込まない。** 本ファイルは複数リポジトリへ複写して使うため、
 * `develop` と決め打つと複写先で静かに誤る。
 *
 * 🔴 **GitHub の「既定ブランチ」設定にも依存しない**（AI レビューの指摘）。
 * Commits API は `sha` を省略すると**既定ブランチ**を見る。本リポジトリでは現在
 * 既定＝`develop` なので実害は無いが、**設定が変われば epoch は別ブランチの値になる**。
 * 実測では `main` に `.github/workflows/ci.yml` を触ったコミットが 1 つも無く、
 * その場合 epoch は null になって**絞り込みが黙って消える**。
 *
 * 母集合の PR の `base.ref` は「その計測がどのブランチ向けに行われたか」そのものであり、
 * リポジトリ設定より確かな情報である。**最頻値**を採る（1 本だけ別ブランチ向けの PR が
 * 混ざっても揺らがない）。1 本も無ければ null＝`sha` を付けない（従来どおり既定ブランチ）。
 */
function integrationBranch(prs) {
  const counts = new Map();
  for (const p of prs || []) {
    const ref = p && p.base && p.base.ref;
    if (!ref) continue;
    counts.set(ref, (counts.get(ref) || 0) + 1);
  }
  let best = null;
  let bestN = 0;
  for (const [ref, n] of counts) {
    if (n > bestN) {
      best = ref;
      bestN = n;
    }
  }
  return best;
}

/**
 * 「CI 構成が最後に変わった時刻」（epoch）を GitHub API から引く。IADR-0241（追随元: AST/IADR-0208 決定 15）。
 *
 * 🔴 **日付を焼き込まない。** 固定値は必ず腐る —— 本検査器が「しきい値を固定値で持たない
 * （自己校正）」と決めたのと同じ理由である。構成ファイルの最終変更時刻なら、
 * CI を触るたびに自動で前進する。
 *
 * 🔴 **`git log` で求めてはならない。** `actions/checkout` は既定で shallow であり、
 * その出力は**履歴の打ち切り位置**を指し得る。しかも形式としては正しい日付が返るため、
 * 誤っていることに気付けない。API なら `contents: read` だけで確実に引ける。
 *
 * 引けなければ null を返す（呼び出し側は絞り込みをやめる＝従来どおり全 PR を母集合にする）。
 * **監視を門に化けさせない** —— ここで赤くすると、速さの監視が可用性の門になる。
 */
async function fetchConfigEpoch({ base, token, path = CONFIG_PATH, branch = null }) {
  if (process.env.CI_LATENCY_EPOCH) return process.env.CI_LATENCY_EPOCH;
  try {
    const sha = branch ? `&sha=${encodeURIComponent(branch)}` : '';
    const commits = await fetchJson(`${base}/commits?path=${encodeURIComponent(path)}&per_page=1${sha}`, token);
    const date =
      Array.isArray(commits) && commits[0] && commits[0].commit && commits[0].commit.committer
        ? commits[0].commit.committer.date
        : null;
    return date && Number.isFinite(Date.parse(date)) ? date : null;
  } catch {
    return null;
  }
}

/**
 * 「この計測は現在の CI 構成を表すか」。
 *
 * 🔴 **マージ時刻だけで判定してはならない**（AI レビューの指摘で是正）。判定したいのは
 * 「どの構成で**測られた**か」であって「いつマージされたか」ではない。
 * **CI が走ったのは構成変更の前、マージはその後**という PR は普通に起こる
 * （PR を放置している間に develop 側で `ci.yml` が変わり、リベースせずマージされた場合）。
 * その 1 本は旧構成の値なのに母集合へ残り、**IADR-0241 が塞ごうとした穴を epoch 自身が再現する**。
 *
 * 🔴 **逆に、計測時刻だけで判定すると今度は取りこぼす**（実データで判明）。
 * **構成変更を持ち込んだ PR 自身**は、その run が**新しい `ci.yml` で走っている**
 * （`pull_request` の run は base を取り込んだ merge ref で動く）のに、
 * epoch＝develop へ載った時刻はその run より**後**になるため落ちてしまう。
 * 実測では AST#546 がこれに当たり、**母集合が 0 本になった**。
 * 落ちるのは「新しい構成での最初の 1 本」＝**最も知りたいサンプル**である。
 *
 * したがって両方を見る。変更ファイル一覧は backend の判定のために既に引いており、追加の API 呼び出しは無い。
 *
 * fail-open（絞り込まない）に倒すのは、epoch が無い・壊れている・計測時刻が取れないときである。
 * 境界（同時刻）は**残す** —— その構成で測られた 1 本目だからである。
 */
function representsCurrentConfig({ t0, mergedAt, changedConfig, epoch }) {
  if (!epoch) return true;
  const e = Date.parse(epoch);
  if (!Number.isFinite(e)) return true;
  if (t0 === null || t0 === undefined) return true; // 計測時刻が取れない
  if (t0 >= e) return true; // 現在の構成で測られた
  // その構成変更を持ち込んだ PR 自身（run は新しい ci.yml で走っている）
  const m = Date.parse(mergedAt);
  if (changedConfig && Number.isFinite(m) && m >= e) return true;
  return false;
}

// PR 一覧のページ上限。100 本 × 10 ページ。既定（N=20・K=3）では 80 本のマージ済み PR が要る。
const PULLS_MAX_PAGES = 10;

async function collect({ repo, token, samples, floorWindows = FLOOR_WINDOWS }) {
  const base = `https://api.github.com/repos/${repo}`;
  // 現在の窓 1 個＋過去の窓 K 個ぶんのマージ済み PR が要る（#1910）。
  // closed には**マージされずに閉じた** PR が混ざるので、マージ済みが要る本数の 1.5 倍に達するまでページを読む
  // （`sort=updated` はマージ順ではないため、末尾の取りこぼしを余裕で吸う）。
  const needed = samples * (floorWindows + 1);
  const prs = [];
  for (let page = 1; page <= PULLS_MAX_PAGES; page += 1) {
    let batch;
    try {
      batch = await fetchJson(`${base}/pulls?state=closed&per_page=100&sort=updated&direction=desc&page=${page}`, token);
    } catch (e) {
      e.scope = 'repo'; // リポジトリ全体が引けない＝設定の誤りとして扱う
      throw e;
    }
    const list = Array.isArray(batch) ? batch : [];
    prs.push(...list);
    if (list.length < 100) break;
    if (prs.filter((p) => p && p.merged_at).length >= needed * 1.5) break;
  }
  const ordered = sortByMergedAtDesc(prs).slice(0, needed);
  const { current: merged, past } = splitWindows(ordered, { samples, floorWindows });

  // 🔴 **現在の CI 構成で測られた PR だけを母集合にする**（IADR-0241）。
  // 中央値は「窓の多数派」を映すので、構成を変えた直後の窓は旧構成の値を現在の値として報告する。
  // 判定は下のループで、**計測時刻（check 群の開始）**に対して行う（マージ時刻ではない）。
  const branch = integrationBranch(merged);
  const epoch = await fetchConfigEpoch({ base, token, branch });
  let skippedPreEpoch = 0;
  if (!epoch) {
    console.log(
      `::notice::[check-ci-latency] ${CONFIG_PATH}（${branch || '既定ブランチ'}）の最終変更時刻を引けなかったため、` +
        'CI 構成による母集合の限定をしていない（従来どおり全 PR を見る）。',
    );
  }

  const targetDurations = [];
  const baselineDurations = [];
  let fetchFailures = 0;
  let missingHeads = 0;
  for (const pr of merged) {
    const sha = pr.head && pr.head.sha;
    if (!sha) continue;

    // 「構成変更を持ち込んだ PR か」を見るために変更ファイル一覧を引く（IADR-0241）。
    // 🔴 **1 ページ目だけ見てはならない。** 変更 100 件を超える PR で構成ファイルが 101 件目以降に
    // あると取りこぼす（AST で実測 —— 変更 619 ファイルの PR が該当した）。
    let changedFiles = null; // 読み切れなかった／引けなかったときは null のまま
    try {
      const { names, complete } = await fetchChangedFiles((page) =>
        fetchJson(`${base}/pulls/${pr.number}/files?per_page=${FILES_PAGE_SIZE}&page=${page}`, token)
      );
      if (complete) changedFiles = names;
    } catch (e) {
      if (classifyFailure(e.status, { rateLimited: e.rateLimited, scope: 'commit' }) === 'config') {
        e.scope = 'commit';
        throw e;
      }
      // 引けなければ諦める。速さの監視を門に化けさせない。
    }

    let runs;
    try {
      runs = await fetchJson(`${base}/commits/${sha}/check-runs?per_page=100`, token);
    } catch (e) {
      // 🔴 権限・認証の誤りは**握りつぶさない**。握りつぶすと「権限設定が恒久的に誤っている」と
      // 「単にマージ済み PR が少ない」が**区別できなくなり**、監視は緑のまま一度も働かない。
      const kind = classifyFailure(e.status, { rateLimited: e.rateLimited, scope: 'commit' });
      if (kind === 'config') {
        e.scope = 'commit';
        throw e;
      }
      // 一過性（5xx・レート制限）と、その 1 本が消えただけ（404）は落として続ける。
      if (kind === 'missing') missingHeads += 1;
      else fetchFailures += 1;
      continue;
    }
    const all = runs.check_runs || [];
    const t0 = checkSetStart(all);
    // 🔴 旧構成で測られた run は母集合に入れない（IADR-0241）。
    // **計測時刻で見る**（マージ時刻ではない）。ただし構成変更を持ち込んだ PR 自身は残す。
    //
    // 🔴 **ここだけ fail-open の向きが逆なのは意図的である。**
    // ファイル一覧が引けなかった（`changedFiles === null`）とき `changedConfig` は false 扱いになり、
    // **計測が epoch より前ならその PR は落ちる**。理由:
    //   - 落とす誤り  … 母集合が 1 本小さくなるだけで、**本数は出力される**（黙っていない）
    //   - 残す誤り    … **旧構成の値が中央値へ混ざる**＝本決定が塞いだ誤報そのものが戻る
    // 「判断材料が欠けたら狭めない」の原則は**黙って狭めないこと**を言っており、
    // ここは数えて出しているので当たらない。**向きを揃えると誤報の側へ倒れる。**
    if (!representsCurrentConfig({
      t0,
      mergedAt: pr.merged_at,
      changedConfig: Boolean(changedFiles && changedFiles.includes(CONFIG_PATH)),
      epoch,
    })) {
      skippedPreEpoch += 1;
      continue;
    }
    for (const r of all) {
      const d = waitSec(r, t0);
      if (d === null) continue;
      if (r.name === TARGET) targetDurations.push(d);
      else if (r.name === BASELINE) baselineDurations.push(d);
    }
  }

  // 🔴 **過去の窓は epoch で絞らない**（#1910。IADR-0241 の 2026-10-12 追記）。
  // `ci.yml` はほぼ毎日変わるので、絞ると床を作れる日が無くなる。床は構成をまたいだ「出せていた速さ」の記録である。
  // 変更ファイル一覧も引かない（epoch の判定に使うだけなので要らない）。
  const pastWindows = [];
  for (const w of past) {
    const values = [];
    for (const pr of w) {
      const sha = pr.head && pr.head.sha;
      if (!sha) continue;
      let runs;
      try {
        runs = await fetchJson(`${base}/commits/${sha}/check-runs?per_page=100`, token);
      } catch (e) {
        const kind = classifyFailure(e.status, { rateLimited: e.rateLimited, scope: 'commit' });
        if (kind === 'config') {
          e.scope = 'commit';
          throw e;
        }
        if (kind === 'missing') missingHeads += 1;
        else fetchFailures += 1;
        continue;
      }
      const all = runs.check_runs || [];
      const t0 = checkSetStart(all);
      for (const r of all) {
        if (r.name !== TARGET) continue;
        const d = waitSec(r, t0);
        if (d !== null) values.push(d);
      }
    }
    pastWindows.push(values);
  }

  return {
    merged: merged.length,
    pastMerged: past.reduce((n, w) => n + w.length, 0),
    targetDurations,
    baselineDurations,
    pastWindows,
    fetchFailures,
    missingHeads,
    skippedPreEpoch,
    epoch,
    branch,
  };
}

async function selfTest() {
  const t = [];
  // 🔴 **非同期の試験を同期のハーネスへ入れてはならない。** `fn()` を await しないと、
  // 中で throw しても**その行は `ok` と表示され、集計も「N 件 OK」と出る**（実測）。
  // 落ちるのは Node が未処理の拒否でプロセスを中断するからであり、
  // **報告と実態が食い違う**（どの試験が失敗したのか読み取れない）。
  // 実測: ページネーションを 1 ページ目で打ち切る変異を入れた状態で、
  // 同期ハーネスは当該試験を `ok` と表示した。非同期対応後は `FAIL` と表示する。
  // 戻り値が thenable なら解決を待ち、その完了を pending へ積んで最後にまとめて待つ。
  const pending = [];
  const ok = (name, fn) => {
    let r;
    try {
      r = fn();
    } catch (e) {
      t.push(`  FAIL ${name}: ${e.message}`);
      process.exitCode = 1;
      return;
    }
    if (r && typeof r.then === 'function') {
      pending.push(
        r.then(
          () => t.push(`  ok   ${name}`),
          (e) => {
            t.push(`  FAIL ${name}: ${e.message}`);
            process.exitCode = 1;
          }
        )
      );
      return;
    }
    t.push(`  ok   ${name}`);
  };
  const eq = (a, b, m) => {
    if (JSON.stringify(a) !== JSON.stringify(b)) throw new Error(`${m || ''} ${JSON.stringify(a)} != ${JSON.stringify(b)}`);
  };

  // fetch の Headers を模す（`get` を持つオブジェクト）。実物の Headers は Node 18+ に有るが、
  // 試験の意図は「`get` で引ける入れ物なら拾えるか」なので最小の模造で足りる。
  const headersOf = (obj) => ({ get: (k) => (k in obj ? obj[k] : null) });
  const okRun = (a, b, extra) => ({ started_at: a, completed_at: b, status: 'completed', conclusion: 'success', ...extra });
  ok('durationSec: 差を秒で返す', () => eq(durationSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:02:00Z')), 120));
  ok('durationSec: 欠損は null', () => eq(durationSec(okRun(null, 'x')), null));
  ok('durationSec: 負値は null（時計のずれを通さない）', () =>
    eq(durationSec(okRun('2026-01-01T00:02:00Z', '2026-01-01T00:00:00Z')), null));
  // 🔴 実データで踏んだ罠の回帰テスト（skip が 0 秒として基準へ混ざり、最小が 0 になった）。
  ok('durationSec: skipped は null（0 秒が最小を汚す）', () =>
    eq(durationSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', { conclusion: 'skipped' })), null));
  ok('durationSec: action_required は null', () =>
    eq(durationSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:01:00Z', { conclusion: 'action_required' })), null));
  ok('durationSec: cancelled は null', () =>
    eq(durationSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:01:00Z', { conclusion: 'cancelled' })), null));
  ok('durationSec: 未完了は null', () =>
    eq(durationSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:01:00Z', { status: 'in_progress' })), null));
  ok('durationSec: 0 秒は null（同時刻の完了を通さない）', () =>
    eq(durationSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')), null));
  ok('durationSec: failure は数える（遅さの観測に成否は関係ない）', () =>
    eq(durationSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:01:00Z', { conclusion: 'failure' })), 60));
  // 🔴 実データで踏んだ罠の回帰テスト（集約ジョブを自身の所要で測り、実際の 1/8 に見えた）。
  ok('checkSetStart: 最小の started_at を返す（skip も含める）', () =>
    eq(
      checkSetStart([
        { started_at: '2026-01-01T00:00:10Z' },
        { started_at: '2026-01-01T00:00:00Z' },
        { started_at: '2026-01-01T00:00:05Z' },
      ]),
      Date.parse('2026-01-01T00:00:00Z')
    ));
  ok('checkSetStart: 空は null', () => eq(checkSetStart([]), null));
  ok('waitSec: 群の開始からの完了オフセットを返す', () => {
    const t0 = Date.parse('2026-01-01T00:00:00Z');
    // 集約ジョブ: +111s に起動し +126s に完了（自身の所要は 15 秒）。
    const agg = okRun('2026-01-01T00:01:51Z', '2026-01-01T00:02:06Z');
    eq(durationSec(agg), 15, '所要');
    eq(waitSec(agg, t0), 126, '待ち時間');
  });
  ok('waitSec: 走らなかった run は null（durationSec と同じ門を通す）', () => {
    const t0 = Date.parse('2026-01-01T00:00:00Z');
    eq(waitSec(okRun('2026-01-01T00:01:51Z', '2026-01-01T00:02:06Z', { conclusion: 'skipped' }), t0), null);
  });
  ok('waitSec: t0 が無ければ null', () =>
    eq(waitSec(okRun('2026-01-01T00:00:00Z', '2026-01-01T00:01:00Z'), null), null));

  // 🔴 AI レビューが両リポジトリで独立に指摘した罠の回帰テスト。
  // permissions が足りないと 403 で早期終了し、fail-open で**緑のまま永久に働かない**。
  ok('classifyFailure: 401 / 403 は設定の誤り（赤くする）', () =>
    eq([401, 403].map((c) => classifyFailure(c)), ['config', 'config']));
  ok('classifyFailure: 5xx・不明は一過性（fail-open のまま）', () =>
    eq([500, 502, 503, undefined, null].map((c) => classifyFailure(c)), ['transient', 'transient', 'transient', 'transient', 'transient']));
  // 🔴 AI レビューの指摘の回帰テスト。403 はレート制限でも返るため、
  // ステータスだけで権限誤りと断じると**待てば直る失敗で誤起票する**。
  ok('classifyFailure: レート制限の 403 は一過性（待てば直るので赤くしない）', () =>
    eq(classifyFailure(403, { rateLimited: true }), 'transient'));
  // 🔴 AI レビューの指摘の回帰テスト。404 の意味は文脈で変わる。
  ok('classifyFailure: リポジトリ側の 404 は設定の誤り（リポジトリ名の誤り・権限なし）', () =>
    eq(classifyFailure(404, { scope: 'repo' }), 'config'));
  ok('classifyFailure: 個別コミットの 404 は「その 1 本が消えただけ」', () =>
    eq(classifyFailure(404, { scope: 'commit' }), 'missing'));
  ok('isRateLimited: x-ratelimit-remaining=0 を拾う', () =>
    eq(isRateLimited(headersOf({ 'x-ratelimit-remaining': '0' })), true));
  ok('isRateLimited: retry-after を拾う', () => eq(isRateLimited(headersOf({ 'retry-after': '60' })), true));
  ok('isRateLimited: 余裕があれば false', () =>
    eq(isRateLimited(headersOf({ 'x-ratelimit-remaining': '4321' })), false));
  ok('isRateLimited: headers が無ければ false', () => eq(isRateLimited(null), false));
  // 🔴 `sort=updated` はマージ後のコメントでも進むため、マージ順とは一致しない。
  ok('sortByMergedAtDesc: merged_at の新しい順に並べ直す', () =>
    eq(
      sortByMergedAtDesc([
        { number: 1, merged_at: '2026-08-20T00:00:00Z' },
        { number: 2, merged_at: '2026-08-22T00:00:00Z' },
        { number: 3, merged_at: '2026-08-21T00:00:00Z' },
      ]).map((p) => p.number),
      [2, 3, 1]
    ));
  ok('sortByMergedAtDesc: マージされずに閉じた PR を落とす', () =>
    eq(
      sortByMergedAtDesc([
        { number: 1, merged_at: null },
        { number: 2, merged_at: '2026-08-22T00:00:00Z' },
      ]).map((p) => p.number),
      [2]
    ));

  ok('median: 奇数個', () => eq(median([3, 1, 2]), 2));
  ok('median: 偶数個は下側（鳴りにくい側へ倒す）', () => eq(median([1, 2, 3, 4]), 2));
  ok('median: 空は null', () => eq(median([]), null));
  ok('min: 最小を返す', () => eq(min([5, 2, 9]), 2));

  // --- #1910: 判定は「監視対象自身の過去の床」。床＝過去 K 窓それぞれの中央値の最小。 ---
  // 試験の既定: K=3・R=1.25。過去の窓は 3 本ずつ（最小本数の門 3 本を満たす）。
  const J = (o) => judge({ floorWindows: 3, floorRatio: 1.25, minSamples: 3, floorMinSamples: 3, ...o });
  const steady = [[100, 104, 108], [98, 100, 103], [101, 105, 110]]; // 窓の中央値 104 / 100 / 105 → 床 100
  ok('🔴 judge: 床の R 倍を超えて伸びたら NG（#1910 の本判定）', () => {
    const r = J({ targetDurations: [128, 130, 133], pastWindows: steady });
    eq(r.skipped, false, '門ではなく本判定に到達していること');
    eq(r.floor, 100, '床');
    eq(r.ok, false, '130 > 100 × 1.25');
  });
  ok('🔴 judge: 緩やかな揺らぎ（床の 1.15 倍）は ok', () => {
    const r = J({ targetDurations: [110, 115, 121], pastWindows: steady });
    eq(r.skipped, false, '門ではなく本判定に到達していること');
    eq(r.ok, true, '115 <= 125');
  });
  ok('🔴 judge: 履歴（床の候補になる過去の窓）が K 個に満たなければ skip（黙って緑にしない）', () => {
    const r = J({ targetDurations: [300, 310, 320], pastWindows: steady.slice(0, 2) });
    eq(r.ok, true);
    eq(r.skipped, true, 'skip として記録されねばならない');
    if (!r.reason.includes('2 個') || !r.reason.includes('3 個要る')) throw new Error(`窓の数が理由に出ていない: ${r.reason}`);
  });
  ok('judge: 過去の窓が最小本数に満たなければ床の候補にしない（履歴の門へ回る）', () => {
    // 3 窓あるが 1 窓は 2 本しかない → 候補 2 窓 < K=3 → skip。
    const r = J({ targetDurations: [300, 310, 320], pastWindows: [[100, 104, 108], [98, 100], [101, 105, 110]] });
    eq(r.skipped, true);
    eq(r.windows, 2);
  });
  ok('🔴 judge: 過去の窓は N の半分（floorMinSamples）以上の値が無ければ床の候補にしない（少数の速い窓で床を固定しない）', () => {
    // N=20 相当（floorMinSamples=10）。3 本だけの速い窓（中央値 60）が床になると 100 前後の現在の窓が誤って鳴る。
    const full = (v) => Array(10).fill(v);
    const r = J({
      targetDurations: [100, 104, 108],
      pastWindows: [[58, 60, 62], full(100), full(102), full(98)],
      floorWindows: 3,
      floorMinSamples: 10,
    });
    eq(r.windows, 3, '3 本の窓を候補に数えている');
    eq(r.floor, 98, '少数の窓が床になっている');
    eq(r.ok, true);
  });
  ok('judge: 履歴が 0 窓でも skip（床 null を判定に使わない）', () => {
    const r = J({ targetDurations: [300, 310, 320], pastWindows: [] });
    eq(r.skipped, true);
    eq(r.ok, true);
  });
  ok('judge: ちょうど R 倍は ok（超えたら赤。境界）', () => {
    // 🔴 対象を 3 本にする理由: 1 本だと最小本数の門が先に効き、**skip の ok:true を「境界が通った」と読み違える**。
    const r = J({ targetDurations: [125, 125, 125], pastWindows: steady });
    eq(r.skipped, false, '門ではなく本判定に到達していること');
    eq(r.ok, true, '125 = 100 × 1.25 は超えていない');
    eq(J({ targetDurations: [126, 126, 126], pastWindows: steady }).ok, false, '1 秒超えたら赤');
  });
  ok('🔴 judge: 床は「過去の窓の中央値の最小」（全値の最小ではない）', () => {
    // 全値の最小は 60（1 本の当たり）。全値の最小を床にすると 60 × 1.25 = 75 < 115 で鳴ってしまう。
    const r = J({ targetDurations: [110, 115, 121], pastWindows: [[60, 104, 108], [98, 100, 103], [101, 105, 110]] });
    eq(r.floor, 100);
    eq(r.ok, true, '全値の最小で見ている');
  });
  ok('🔴 judge: 床は「過去の窓の中央値の最小」（中央値の中央値ではない）', () => {
    // 窓の中央値 100 / 150 / 160。中央値の中央値は 150 で 140 は通るが、床 100 × 1.25 = 125 < 140 で落ちるのが正しい。
    const r = J({ targetDurations: [138, 140, 142], pastWindows: [[98, 100, 103], [148, 150, 152], [158, 160, 162]] });
    eq(r.floor, 100);
    eq(r.ok, false, '最小で見ていない');
  });
  ok('judge: 監視対象は最小ではなく中央値で見る（外れ値で鳴らさない・外れ値で見逃さない）', () => {
    // 対象の最小は 90 だが中央値は 140。最小で見れば通り（90 <= 125）、中央値で見れば落ちる（140 > 125）。
    // 🔴 開き幅を 1.6 倍に抑えてあるのは、定常性の門（2 倍）に掛からせないためである。
    const r = J({ targetDurations: [90, 140, 150], pastWindows: steady });
    eq(r.skipped, false, '門で skip されてはならない');
    eq(r.ok, false);
  });
  ok('🔴 judge: claude-review との逆転は判定に使わない（参考値として返すだけ）', () => {
    // 旧方式なら 115 > 70 で逆転＝NG。新方式では床 100 × 1.25 = 125 の内なので ok。
    const r = J({ targetDurations: [110, 115, 121], baselineDurations: [70, 88, 120], pastWindows: steady });
    eq(r.skipped, false);
    eq(r.ok, true, 'claude-review の下限で判定している（旧方式に戻っている）');
    eq(r.baseline, 70, '参考値（claude-review の最小）');
    eq(r.reversed, true, '参考値（逆転の有無）');
  });
  ok('judge: 逆転していなくても床から伸びれば NG（基準は claude-review ではない）', () => {
    const r = J({ targetDurations: [128, 130, 133], baselineDurations: [400], pastWindows: steady });
    eq(r.reversed, false);
    eq(r.ok, false);
  });
  ok('judge: R と K は引数で効く', () => {
    eq(J({ targetDurations: [128, 130, 133], pastWindows: steady, floorRatio: 1.35 }).ok, true, 'R=1.35 なら 130 <= 135');
    eq(J({ targetDurations: [128, 130, 133], pastWindows: steady, floorWindows: 4 }).skipped, true, 'K=4 なら 3 窓では足りない');
  });
  // 🔴 実データで踏んだ罠の回帰テスト（最適化直後、12 本中 11 本が旧構成で中央値が旧値を指した）。
  ok('judge: 構成変更をまたいだサンプルは判定しない（狼少年にしない）', () => {
    // 旧構成 447 秒が 11 本・新構成 126 秒が 1 本。中央値 447 は現在の構成を表していない。
    const old11 = Array(11).fill(447);
    const r = J({ targetDurations: [...old11, 126], pastWindows: steady });
    eq(r.ok, true, '鳴ってはならない');
    eq(r.skipped, true, 'skip として記録されねばならない');
  });
  ok('judge: 窓が入れ替わったら判定する（門は恒久的に黙らせない）', () => {
    const r = J({ targetDurations: [120, 126, 140], pastWindows: [[120, 126, 130], [118, 125, 131], [121, 127, 135]] });
    eq(r.skipped, false, '判定されねばならない');
    eq(r.ok, true);
  });
  ok('judge: ランナーのばらつき（1.2 倍）は定常性の門を通す', () => {
    const r = J({ targetDurations: [409, 447, 479], pastWindows: steady });
    eq(r.skipped, false, '1.2 倍で skip してはならない');
    eq(r.ok, false);
  });
  ok('judge: サンプル不足は fail-open だが skipped を立てる', () => {
    const r = J({ targetDurations: [], baselineDurations: [148], pastWindows: steady });
    eq(r.ok, true);
    eq(r.skipped, true);
  });
  ok('floorOf: 窓の中央値の最小と、候補の窓の数を返す', () => {
    const f = floorOf([[100, 104, 108], [98, 100], [101, 105, 110]], { minSamples: 3 });
    eq(f.floor, 104);
    eq(f.windows, 2);
  });
  ok('floorOf: 候補が無ければ null', () => eq(floorOf([], { minSamples: 3 }).floor, null));

  // 窓の切り方（#1910）。境界はマージ順だけで決まり、過去の窓は短くても返す。
  ok('splitWindows: 先頭 N 本が現在、続く N 本ずつが過去の窓（K 個まで）', () => {
    const prs = Array.from({ length: 10 }, (_, i) => i);
    const { current, past } = splitWindows(prs, { samples: 2, floorWindows: 3 });
    eq(current, [0, 1]);
    eq(past, [[2, 3], [4, 5], [6, 7]]);
  });
  ok('splitWindows: PR が足りなければ過去の窓は少ない（履歴の門で skip される）', () => {
    const { past } = splitWindows([0, 1, 2, 3, 4], { samples: 2, floorWindows: 3 });
    eq(past, [[2, 3], [4]]);
  });
  ok('validateConfig: 既定値は正しい', () => eq(validateConfig({ floorWindows: 3, floorRatio: 1.25, samples: 20 }), null));
  ok('🔴 validateConfig: 門の値（MIN_SAMPLES・FLOOR_MIN_SAMPLES・STEP_RATIO）の不正も設定の誤り（NaN で門を素通りさせない）', () => {
    const good = { floorWindows: 3, floorRatio: 1.25, samples: 20, minSamples: 3, floorMinSamples: 10, stepRatio: 2 };
    eq(validateConfig(good), null);
    for (const bad of [
      { minSamples: NaN }, { minSamples: 0 }, { minSamples: 2.5 },
      { floorMinSamples: NaN }, { floorMinSamples: 0 },
      { stepRatio: NaN }, { stepRatio: 1 }, { stepRatio: 0.5 },
    ]) {
      if (validateConfig({ ...good, ...bad }) === null) throw new Error(`誤りとして扱われない: ${JSON.stringify(bad)}`);
    }
  });
  ok('🔴 validateConfig: R ≦ 1・K < 1・数値でない値は設定の誤り（NaN 比較で常に緑にしない）', () => {
    for (const bad of [
      { floorWindows: 3, floorRatio: 1, samples: 20 },
      { floorWindows: 3, floorRatio: NaN, samples: 20 },
      { floorWindows: 0, floorRatio: 1.25, samples: 20 },
      { floorWindows: 1.5, floorRatio: 1.25, samples: 20 },
      { floorWindows: 3, floorRatio: 1.25, samples: 0 },
    ]) {
      if (validateConfig(bad) === null) throw new Error(`誤りとして扱われない: ${JSON.stringify(bad)}`);
    }
  });

  // 🔴 ページネーションの回帰テスト。**1 ページ目だけ見ると、構成ファイルの変更が
  // 101 件目以降にある PR を「構成変更を持ち込んだ PR ではない」と誤判定する。**
  const page = (n, prefix) => Array.from({ length: n }, (_, i) => ({ filename: `${prefix}${i}.md` }));
  ok('fetchChangedFiles: 1 ページで終わる場合', async () => {
    const r = await fetchChangedFiles(() => page(3, 'docs/a'));
    eq(r.names.length, 3);
    eq(r.complete, true);
  });
  ok('fetchChangedFiles: ちょうど 100 件なら次ページを引く', async () => {
    const calls = [];
    const r = await fetchChangedFiles((p) => {
      calls.push(p);
      return p === 1 ? page(100, 'docs/a') : [];
    });
    eq(calls.join(','), '1,2');
    eq(r.names.length, 100);
    eq(r.complete, true);
  });
  ok('🔴 fetchChangedFiles: 101 件目以降の構成ファイル変更を取りこぼさない', async () => {
    const r = await fetchChangedFiles((p) =>
      p === 1 ? page(100, 'docs/a') : [{ filename: CONFIG_PATH }]
    );
    eq(r.complete, true);
    eq(r.names.includes(CONFIG_PATH), true, '2 ページ目を読んでいない');
  });
  ok('参考: 1 ページ目だけ見ると構成ファイルを見落とす（回帰の再現）', () =>
    eq(page(100, 'docs/a').map((f) => f.filename).includes(CONFIG_PATH), false));
  ok('fetchChangedFiles: 上限まで読んでも終わらなければ complete=false', async () => {
    const r = await fetchChangedFiles(() => page(100, 'docs/a'), { maxPages: 3 });
    eq(r.complete, false);
    eq(r.names.length, 300);
  });
  ok('fetchChangedFiles: 配列以外が返っても落ちない（complete で扱う）', async () => {
    const r = await fetchChangedFiles(() => null);
    eq(r.names.length, 0);
    eq(r.complete, true);
  });

  const EPOCH = '2026-08-22T00:15:00Z';
  const at = (iso) => Date.parse(iso);
  const rep = (o) => representsCurrentConfig({ changedConfig: false, mergedAt: null, epoch: EPOCH, ...o });
  ok('representsCurrentConfig: epoch より前に測られた run は落とす', () =>
    eq(rep({ t0: at('2026-08-21T00:00:00Z') }), false));
  ok('representsCurrentConfig: epoch 以降に測られた run は残す', () =>
    eq(rep({ t0: at('2026-08-23T00:00:00Z') }), true));
  ok('representsCurrentConfig: 境界（epoch と同時刻）は残す', () =>
    eq(rep({ t0: at(EPOCH) }), true, 'その構成で測られた 1 本目である'));
  ok('🔴 representsCurrentConfig: epoch が無ければ絞り込まない（fail-open）', () =>
    eq(rep({ t0: at('2020-01-01T00:00:00Z'), epoch: null }), true));
  ok('representsCurrentConfig: 壊れた epoch でも絞り込まない（fail-open）', () =>
    eq(rep({ t0: at('2020-01-01T00:00:00Z'), epoch: 'not-a-date' }), true));
  ok('representsCurrentConfig: 計測時刻が取れなければ絞り込まない（fail-open）', () =>
    eq(rep({ t0: null }), true));
  // 🔴 AI レビューの指摘の回帰テスト。**マージ時刻だけで判定していたら通ってしまう形。**
  ok('🔴 representsCurrentConfig: 「CI は構成変更の前、マージはその後」の PR を落とす', () =>
    eq(
      rep({ t0: at('2026-08-21T12:00:00Z'), mergedAt: '2026-08-23T00:00:00Z' }),
      false,
      '計測時刻で見ていない',
    ));
  // 🔴 実データで判明した取りこぼしの回帰テスト。**計測時刻だけで判定していたら落ちてしまう形。**
  ok('🔴 representsCurrentConfig: 構成変更を持ち込んだ PR 自身は残す（実測 AST#546）', () =>
    eq(
      rep({ t0: at('2026-08-21T23:57:00Z'), mergedAt: '2026-08-22T00:15:29Z', changedConfig: true }),
      true,
      'run は新しい ci.yml で走っている。最も知りたい 1 本目を落としてはならない',
    ));
  // 🔴 AI レビューの指摘の回帰テスト。epoch を**どのブランチで測るか**。
  const withBase = (ref) => ({ base: { ref } });
  ok('integrationBranch: 母集合の PR がマージされた先を採る', () =>
    eq(integrationBranch([withBase('develop'), withBase('develop')]), 'develop'));
  ok('integrationBranch: 最頻値を採る（1 本の別ブランチで揺らがない）', () =>
    // 🔴 **先頭を別ブランチにしてある。** `[develop, main, develop]` だと
    // 「先頭を採る」実装でも通ってしまい、**最頻値を採っていることを検査できない**（実測）。
    eq(integrationBranch([withBase('main'), withBase('develop'), withBase('develop')]), 'develop'));
  ok('integrationBranch: 1 本も無ければ null（＝既定ブランチに委ねる）', () => {
    eq(integrationBranch([]), null);
    eq(integrationBranch(null), null);
    eq(integrationBranch([{}, { base: {} }]), null);
  });
  ok('🔴 fetchConfigEpoch: ブランチを渡したら sha としてクエリへ載せる', async () => {
    const orig = process.env.CI_LATENCY_EPOCH;
    delete process.env.CI_LATENCY_EPOCH;
    const seen = [];
    const origFetch = global.fetch;
    global.fetch = async (url) => {
      seen.push(String(url));
      return {
        ok: true,
        headers: { get: () => null },
        json: async () => [{ commit: { committer: { date: '2026-08-22T00:15:29Z' } } }],
      };
    };
    try {
      const e = await fetchConfigEpoch({ base: 'https://api.example/repos/o/r', token: 'x', branch: 'develop' });
      eq(e, '2026-08-22T00:15:29Z');
      if (!seen[0].includes('&sha=develop')) throw new Error(`sha が載っていない: ${seen[0]}`);
    } finally {
      global.fetch = origFetch;
      if (orig === undefined) delete process.env.CI_LATENCY_EPOCH;
      else process.env.CI_LATENCY_EPOCH = orig;
    }
  });
  ok('fetchConfigEpoch: ブランチが無ければ sha を付けない（既定ブランチに委ねる）', async () => {
    const orig = process.env.CI_LATENCY_EPOCH;
    delete process.env.CI_LATENCY_EPOCH;
    const seen = [];
    const origFetch = global.fetch;
    global.fetch = async (url) => {
      seen.push(String(url));
      return { ok: true, headers: { get: () => null }, json: async () => [] };
    };
    try {
      eq(await fetchConfigEpoch({ base: 'https://api.example/repos/o/r', token: 'x', branch: null }), null);
      if (seen[0].includes('sha=')) throw new Error(`sha が載ってしまっている: ${seen[0]}`);
    } finally {
      global.fetch = origFetch;
      if (orig === undefined) delete process.env.CI_LATENCY_EPOCH;
      else process.env.CI_LATENCY_EPOCH = orig;
    }
  });
  ok('representsCurrentConfig: 古い構成変更 PR は残さない（マージも epoch より前）', () =>
    eq(
      rep({ t0: at('2026-08-19T00:00:00Z'), mergedAt: '2026-08-19T01:00:00Z', changedConfig: true }),
      false,
    ));

  ok('🔴 judge: 対象が最小本数に満たなければ判定しない', () => {
    const r = J({ targetDurations: [120, 130], pastWindows: steady, epoch: EPOCH });
    eq(r.skipped, true);
    if (!r.reason.includes('2 本しかない')) throw new Error(`本数が理由に出ていない: ${r.reason}`);
    if (!r.reason.includes(EPOCH)) throw new Error(`epoch が理由に出ていない: ${r.reason}`);
  });
  ok('🔴 judge: 本数が足りれば判定する（skip しっぱなしでない）', () => {
    const r = J({ targetDurations: [120, 130, 125], pastWindows: steady, epoch: EPOCH });
    eq(r.skipped, false);
    eq(r.ok, true, '125 秒 <= 100 × 1.25');
  });
  ok('judge: 本数が足りていれば床からの伸びを検出する', () => {
    const r = J({ targetDurations: [200, 210, 205], pastWindows: steady, epoch: EPOCH });
    eq(r.skipped, false);
    eq(r.ok, false);
  });
  ok('judge: 最小本数の門は定常性の門・履歴の門より先に効く', () => {
    // 中央値 240 / 最小 20 ＝ 12 倍・履歴 0 窓。すべての門に当たるが、報告すべきは「本数が足りない」ほうである。
    const r = J({ targetDurations: [20, 240], pastWindows: [], epoch: EPOCH });
    eq(r.skipped, true);
    if (!r.reason.includes('本しかない')) throw new Error(`理由が違う: ${r.reason}`);
  });
  ok('judge: 定常性の門は履歴の門より先に効く（構成変更の直後は「履歴不足」と誤って言わない）', () => {
    const r = J({ targetDurations: [100, 447, 447], pastWindows: [] });
    eq(r.skipped, true);
    if (!r.reason.includes('構成の変更をまたいでいる')) throw new Error(`理由が違う: ${r.reason}`);
  });
  ok('judge: epoch 不明でも本数の門は効く（理由にその旨が出る）', () => {
    const r = J({ targetDurations: [120], pastWindows: steady, epoch: null });
    eq(r.skipped, true);
    if (!r.reason.includes('epoch 不明')) throw new Error(`理由が違う: ${r.reason}`);
  });
  ok('CI_LATENCY_EPOCH が指定されていれば API を叩かずそれを使う', async () => {
    const orig = process.env.CI_LATENCY_EPOCH;
    process.env.CI_LATENCY_EPOCH = EPOCH;
    try {
      // token/base を渡さずとも API を叩かないことが、例外が出ないことで分かる。
      eq(await fetchConfigEpoch({ base: 'http://invalid.invalid', token: 'x' }), EPOCH);
    } finally {
      if (orig === undefined) delete process.env.CI_LATENCY_EPOCH;
      else process.env.CI_LATENCY_EPOCH = orig;
    }
  });
  await Promise.all(pending);
  console.log(t.join('\n'));
  console.log(`[check-ci-latency] 自己試験 ${t.length} 件${process.exitCode ? ' に失敗あり' : ' OK。'}`);
}

async function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) return await selfTest();

  const reportOnly = argv.includes('--report-only');
  // 🔴 設定値の誤りは fail-open しない（validateConfig の説明を参照）。
  const configError = validateConfig();
  if (configError) {
    console.log(`::error::[check-ci-latency] 設定の誤り: ${configError}。**監視が動いていない。**`);
    process.exitCode = 1;
    return;
  }
  const repo = process.env.GITHUB_REPOSITORY;
  const token = process.env.GITHUB_TOKEN;
  if (!repo || !token) {
    // fail-open。CI 以外（ローカル）で叩いたときに赤くしない。
    console.log('[check-ci-latency] GITHUB_REPOSITORY / GITHUB_TOKEN が無いため skip した（fail-open）。');
    return;
  }

  const {
    merged,
    pastMerged,
    targetDurations,
    baselineDurations,
    pastWindows,
    fetchFailures,
    missingHeads,
    skippedPreEpoch,
    epoch,
    branch,
  } = await collect({ repo, token, samples: SAMPLES, floorWindows: FLOOR_WINDOWS });
  const r = judge({ targetDurations, baselineDurations, pastWindows, epoch });

  // 内訳は 1 つの括弧へまとめる（両方非ゼロのとき括弧が区切りなく並んで読みにくいため）。
  const notes = [];
  if (fetchFailures) notes.push(`${fetchFailures} 本は一過性の API 失敗で取得できず`);
  if (missingHeads) notes.push(`${missingHeads} 本は head が既に無く 404（GC 済みのため恒久的にこのまま）`);
  // 🔴 黙って母集合を狭めない。何本外したかを必ず出す。
  if (skippedPreEpoch) {
    notes.push(
      `${skippedPreEpoch} 本は現在の CI 構成より前（${branch || '既定ブランチ'} の ${CONFIG_PATH} が最後に変わった ${epoch} より前）に測られたので現在の窓から外した`,
    );
  }
  console.log(
    `[check-ci-latency] 現在の窓: マージ済み PR ${merged} 本を走査: ` +
      `${TARGET} ${targetDurations.length} 件 / ${BASELINE} ${baselineDurations.length} 件` +
      (notes.length ? `（うち ${notes.join('、')}）` : ''),
  );
  const fmt = (x) => (x === null || x === undefined ? '—' : `${Math.round(x)}`);
  console.log(
    `[check-ci-latency] 過去の窓: マージ済み PR ${pastMerged} 本を ${SAMPLES} 本ずつ ${pastWindows.length} 窓（要 ${FLOOR_WINDOWS} 窓。各 ${FLOOR_MIN_SAMPLES} 本以上。epoch では絞らない）: ` +
      `${TARGET} の件数 [${pastWindows.map((w) => w.length).join(', ')}] / 中央値 [${pastWindows
        .map((w) => (w.length >= FLOOR_MIN_SAMPLES ? fmt(median(w)) : '—'))
        .join(', ')}] 秒`,
  );
  // 🔴 該当 run が 0 件なら、まず **check 名の設定ミス**を疑わせる。
  // 既定値のまま配備すると 0 件になり、fail-open で**緑のまま永久に skip する**。
  // 🔴 **ただし epoch で外したことが原因なら、この警告を出してはならない。**
  // 原因が分かっているのに「設定ミスを疑え」と言うと、**読み手を誤った方向へ差し向ける**。
  if ((!targetDurations.length || !baselineDurations.length) && !skippedPreEpoch) {
    console.log(
      `[check-ci-latency] ⚠️ 該当 run が 0 件の側がある。CI_LATENCY_TARGET（現在 ${TARGET}）/ ` +
        `CI_LATENCY_BASELINE（現在 ${BASELINE}）が**このリポジトリの check 名と一致しているか**を確かめること。`,
    );
  }
  // 参考値（判定には使わない）。旧方式（〜2026-10-12）の「逆転」。漂流（床が一緒に上がる遅い伸び）は判定に現れないので、ここを人が見る。
  if (r.target !== null && r.baseline !== null) {
    console.log(
      `[check-ci-latency] 参考値（判定には使わない）: ${TARGET} 中央値 ${fmt(r.target)} 秒 / ${BASELINE} 最小 ${fmt(r.baseline)} 秒` +
        `（${r.reversed ? '逆転している' : '逆転していない'}。IADR-0232 決定 8 の 2026-10-12 追記により判定から外した）`,
    );
  }
  if (r.skipped) {
    console.log(`[check-ci-latency] 判定を skip した（${r.reason}）。`);
    console.log('[check-ci-latency] **緑は「伸びていない」を意味しない。判定していないだけである。**');
    return;
  }
  const limit = r.floor * FLOOR_RATIO;
  console.log(
    `[check-ci-latency] ${TARGET} の中央値 ${fmt(r.target)} 秒  vs  自身の過去の床 ${fmt(r.floor)} 秒 × ${FLOOR_RATIO} = ${fmt(limit)} 秒` +
      `（床の ${r.ratio.toFixed(2)} 倍。過去 ${r.windows} 窓の中央値の最小）`,
  );

  if (r.ok) {
    console.log('[check-ci-latency] OK: 床から伸びていない。');
    return;
  }

  console.log(`::error::[check-ci-latency] ${TARGET}（中央値 ${fmt(r.target)} 秒）が自身の過去の床（${fmt(r.floor)} 秒）の ` +
    `${FLOOR_RATIO} 倍（${fmt(limit)} 秒）を超えた（${r.ratio.toFixed(2)} 倍）。直近の窓で待ち時間が段差で伸びている。`);
  console.log('[check-ci-latency] 次の手: 伸びた窓のマージ（特に ci.yml・テストの追加）を探し、律速の脚を割る ' +
    '（IADR-0232 の 2026-09-28 / 10-02 追記: テストのシャーディング・閉包だけのビルド）。' +
    `意図した増加なら、過去の窓が入れ替わる（約 ${SAMPLES * (FLOOR_WINDOWS + 1)} 本のマージ）まで赤が続く。`);
  if (!reportOnly) process.exitCode = 1;
}

main().catch((e) => {
  // 🔴 **権限・認証の誤りは赤くする。** これは「CI が遅い」ではなく「**監視が壊れている**」であり、
  // fail-open で緑を返せば、監視は一度も働かないまま緑を返し続ける
  // （＝「監視が黙って壊れるのは監視が無いより悪い」そのもの）。
  if (classifyFailure(e.status, { rateLimited: e.rateLimited, scope: e.scope || 'repo' }) === 'config') {
    console.log(
      `::error::[check-ci-latency] GitHub API が ${e.status} を返した。**監視が動いていない。** ` +
        'ワークフローの permissions を確かめること —— 本検査器は Pulls API（pull-requests: read）と ' +
        'Checks API（checks: read）を叩く。actions: read では足りない。',
    );
    console.log(`[check-ci-latency] 詳細: ${e.message}`);
    process.exitCode = 1;
    return;
  }
  // 一過性のネットワーク・API 失敗では赤くしない（fail-open）。ただし黙らない。
  console.log(`[check-ci-latency] 実行できなかったため skip した（fail-open）: ${e.message}`);
});
