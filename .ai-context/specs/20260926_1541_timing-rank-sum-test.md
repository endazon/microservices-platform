---
title: 作業仕様書 — T-25 の所要時間を順位和検定（両側・有意水準 1%）で判定し、整数 ns の時計を同時に入れる（#1541・計画 ADR-0113 決定 1〜4）
type: spec
status: done
related_ids:
  - SC-15
  - NFR-13
  - ADR-0094
  - ADR-0103
  - ADR-0108
  - ADR-0113
  - IADR-0432
  - IADR-0463
  - IADR-0470
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0113_timing-judgement-rank-sum-test.md (Accepted 2026-09-26)
  - planning:projects/microservices-platform/07_adr/ADR-0108_timing-samples-at-microsecond-resolution.md (Accepted・2026-09-26 改訂注記)
  - planning:projects/microservices-platform/07_adr/ADR-0094_existence-hiding-timing-median-consistency-and-response-floor.md
  - planning:projects/microservices-platform/07_adr/ADR-0103_degenerate-self-control-is-not-a-bound.md
related_specs:
  - 20260926_1546_timing-clock-revert-to-ms
  - 20260926_1525_timing-resolution-t25
  - 20260926_1542_plan-adr-range-0113
issue: "#1541"
---

# 作業仕様書 — T-25 を順位和検定で判定し、整数 ns の時計を同時に入れる

## 目的と射程

計画 ADR-0113（planning#659 の裁定・planning#661）の決定 1〜4 を `scripts/check-password-reset-mail.js` の T-25 に実装する。

| 計画の決定 | 実装 |
| --- | --- |
| 決定 1: 暖機を除いた反復の標本をまとめ、実在と非実在を順位和検定（両側）で比べ、p < 0.01 なら `不合格`。計算法（正確法か正規近似か・同順位の扱い）は実装が選び IADR に残す | `rankSumTest`（正確法・中間順位）＋ `evaluateTimingConsistency`。計算法は **IADR-0470** 決定 1・2 |
| 決定 2: 片側 12・反復 3（1 回目は暖機）。検定は片側 24 | `TIMING_SAMPLES_PER_SIDE = 12` / `TIMING_REPETITIONS = 3`。暖機を除く 2 反復を連結 |
| 決定 3: 段 1 と「比が自己対照を超えない」を判定から外す。自己対照は `評価不能` にだけ使う（境界 2 倍） | 段 1・段 2 のコードを撤去。自己対照 ≧ 2 倍 → 不合格でなければ `評価不能` |
| 決定 4: 整数 ns の時計を判定式と同時に入れる | `timingClockNs` / `elapsedMsBetween` / `TIMING_SAMPLE_RESOLUTION_MS = 1 / NS_PER_MS` と固定の自己試験を戻した（#1546 で差し戻したもの） |
| フォローアップ 1: 入れる前に合成試験（①系統差 0 の不合格 ≈ 1% ②0.1 ms・1 ms・床超過の検出率）を回し、IADR-0432 の後継へ記録 | 下の §合成試験。**試作で先に回し**、実装後に実装の判定関数で同じ数値を再現。記録は IADR-0470 |
| フォローアップ 2: integration-stack が検査器の段へ届いたら CI の実測（不合格の頻度・p 値の分布）を環流 | 出力へ p 値の行を足し、§CI 実測の環流の雛形 を置いた（届いたら起票） |

**射程外**: ログイン経路（ADR-0094 決定 4 の対象外。`check-login-existence-disclosure.js` は所要時間を出すだけで判定しない）。
床の器・床の値（ADR-0094 決定 2 / ADR-0111）。

**前提**: #1545（計画 ADR レンジを 0113 まで）と #1547（時計の差し戻し）の上に積む。実装 IADR 番号は push 時点の develop の最大（0465）と
開いている PR の新規 IADR（#1539 = 0466・#1548 = 0467）の次の 0468（着手時は 0467 を採ったが、push 直前に #1548 が 0467 で開いたため改番した）。
［2026-09-26 追記 / #1541］#1556 が 0468・#1555 が 0469 を採り先にマージされる順となったため、**0470** へ再度改番した（ファイル名・索引・
関連 IADR の related_ids と本文・`docs/` の trace ブロック・コード内コメント・本仕様書）。#1548・#1556・#1555 が先にマージされるまで
`check-adr-numbering` は 0467〜0469 の欠番を指摘する。

## 実装上の判断（詳細は IADR-0470）

1. **正確法**: 全 N 標本の 2 倍の中間順位から m 個を選ぶ組を順位和ごとに数える（部分和の DP。最大 C(48, 24) ≈ 3.2e13 で倍精度の整数に収まる）。
   帰無分布は「標本数と同順位の構造」を鍵にキャッシュ。同順位なしの片側 24 は 1 度だけ数える。
2. **同順位**: 中間順位。帰無分布も同じ中間順位から数えるので条件付きの正確検定のまま。連続性補正は要らない。
3. **両側 p**: 帰無分布で |W − E[W]| が観測値以上になる割合（m = n なら片側の 2 倍と一致）。
4. **優先**: `不合格`（p < 0.01）＞ `評価不能`（自己対照 ≧ 2 倍、または群が中央値を持てない）＞ `合格`。
5. **出力**: 反復ごとの n・中央値・比・自己対照（比は「判定には使わない」と明記）、まとめた標本数・W と期待値・U・p・有意水準の 1 行。戻り値に `rankSum`。
6. **格子の前提**（`IADR-0463` 決定 4）は残す。順位和は大小しか使わないので実効の粒度が粗くても同順位が増えるだけ。

## 母集合（規則 1〜6・9・10）

**軸 1（誤りの側＝旧判定式の語。判定を扱う文書へ絞る前に、判定の語を含むファイルをパスで引いた）**:

```console
$ git grep -l -E "自己対照|T-25|evaluateTimingConsistency|所要時間の(軸|判定)" -- . ':!src/ai-stock-trading' ':!CHANGELOG.md'
```

58 ファイル（本仕様書を足す前の追跡下。本仕様書を足すと 59）。うち `docs/tests/` の FR / UC / SC-12・17・21・22 各仕様書と `src/` の C# テスト（計 32 件。
`grep -c -E "^docs/tests/(FR|UC|SC-1[27]|SC-2)|^src/"`）は**別画面・別機能の項目 ID `T-25`**（各仕様書の採番）への一致であり除外。
`.ai-context/adr/IADR-0340` と `.ai-context/specs/` の 4 件（`20260818_issue-863`・`20260821_issue-440`・`20260902_571`・`20260903_issue-1120`）も別件の `T-25` への一致で除外。残り:

| ファイル | 扱い |
| --- | --- |
| `scripts/check-password-reset-mail.js` | **対象**（判定・定数・時計・自己試験・run の告知） |
| `docs/screens/SC-15_password-reset.md` §所要時間の差 | **対象**（判定条件・経緯・合成試験・受け入れたリスク・赤の読み方） |
| `docs/tests/SC-15_password-reset.md` T-25 行と注記 | **対象** |
| `scripts/README.md` の `check-password-reset-mail.js` 行 | **対象**（判定の説明・自己試験 56 → 54 件） |
| `.github/workflows/integration-stack.yml:240-247` | **対象**（赤の読み方に偶然の赤 ≈ 1% と p 値を足した。コメントのみ。起動条件・ジョブ名は不変） |
| `docs/screens/SC-13_login.md:155` / `docs/tests/SC-13_login.md:145` | **対象**（「定まった条件は比が自己対照を超えないこと」—— リセット経路の条件の説明。日付つき追記で順位和検定へ改まったことと、反復を前提とする点は同じことを足した） |
| `.ai-context/adr/IADR-0432` / `IADR-0463` | **対象**（日付つき追記・related_ids） |
| `.ai-context/adr/README.md` | **対象**（IADR-0470 の索引行） |
| `deploy/mail-relay/reset-floor/reset-floor.yaml:33`（「比 1.00 倍が自己対照の内側」） | **除外**。過去の CI 実測（#1500）の記録であり、当時の判定の値として正しい |
| `.ai-context/adr/IADR-0427`（ログイン経路の測定器） | **除外**。所要時間を判定しない（T-12 は出すだけ） |
| `docs/how-to/plan-id-range-history-annex.md` | **除外**。レンジの追随記録 |
| `.ai-context/specs/` の確定済み 8 件（`20260911_issue-1245_…`・`20260911_issue-1410_…` 2 件・`20260925_1470_…`・`20260926_1519_…`・`20260926_1525_…`・`20260926_1542_…`・`20260926_1546_…`） | **除外**。確定済みの記録（`1546` は本 PR の前段の記録として当時の値で正しい） |

**軸 2（コード側の識別子）**: `git grep -n -E "selfControlStepMs|toHalfTicks|stage|MS_CLOCK|NS_GRID|TIMING_SAMPLES_PER_SIDE" -- scripts` ——
`check-password-reset-mail.js` の外に参照は無い（`module.exports` の利用者は無し。`scripts.test.js` は `--self-test` を呼ぶだけ）。

**軸 3（数値）**: 「片側 6」「18 通」「各群 3 標本」「刻み 1 ns / 1 ms」「62%」を判定を扱う上の対象ファイルで引き直した。
SC-15 画面仕様書・テスト仕様書の旧記述は §経緯 へまとめ直し、現行として「片側 6」「刻み」を書く箇所は残っていない。

**規則 10（この変更で新たに誤りになる自分の記述）**: #1546 で書いた「整数 ms の間は…」「判定式の変更までは整数 ms」（SC-15 の 2 文書・`scripts/README.md`・検査器のコメント）は
本 PR で現行でなくなるため、すべて書き換えた（`git grep -n "判定式の変更まで\|整数 ms で測る" -- docs scripts` が 0 件）。
IADR-0463 の #1546 追記（「後継 IADR が持つ」）は本 PR の追記で閉じた。

## 合成試験（ADR-0113 フォローアップ 1）

### 手順

1. **実装の前に試作で回した**: 順位和検定（正確法）を scratch の試作モジュールに書き、全組合せの総当たり（同順位あり・m ≠ n の 400 例）と一致することを確かめてから、下の条件で回した。
2. 実装後、**実装の `evaluateTimingConsistency` をそのまま呼んで**同じ seed で回し、**試作と全数値が一致**した（下表は実装の値）。

条件は ADR-0113 実測 4 と同じ: 床 150 ms ＋ 揺れ、実在・非実在は同じ分布から独立（系統差 0）または実在側にだけ差を足す。
反復 3（1 回目は暖機）× 片側 12、1 反復の中で実在 → 非実在を交互に取る。標本は整数 ns へ丸める。**各 20,000 実行**、mulberry32・seed 5001 から順に固定。

### ① 系統差 0 の不合格率と p 値の分布（半正規の揺れ）

| 揺れ sd | 不合格 | 評価不能 | p < 0.05 | p < 0.10 | p < 0.50 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 20 µs | **1.01%** | 0.00% | 4.83% | 9.79% | 49.18% |
| 100 µs | **1.13%** | 0.00% | 4.93% | 9.83% | 49.20% |
| 300 µs | **0.95%** | 0.00% | 4.95% | 9.77% | 49.95% |
| 1 ms | **0.96%** | 0.00% | 4.96% | 10.05% | 49.41% |

20,000 実行の不合格率の標準誤差は約 0.07 ポイント。p 値はほぼ一様に分布する。

### ① 系統差 0・雑音の形を替える（sd 300 µs 相当）

| 雑音 | 不合格 | 評価不能 |
| --- | ---: | ---: |
| 指数分布 | 1.08% | 0.00% |
| 対数正規 | 0.97% | 0.00% |
| 5% の外れ値（sd の 20 倍） | 0.94% | 0.00% |
| 反復内のドリフト（1 標本ごとに 20 µs ずつ遅くなる・半正規 300 µs） | 0.17% | 0.00% |

ドリフトで下がるのは、交互に取ると両側へ同じ傾きが乗り、順位が反復内で入れ子になるため（保守側）。ADR-0113 実測 5 の 0.0% と同じ向き。

### ② 既知の系統差の検出率（半正規の揺れ）

| 揺れ sd | 差 0.1 ms | 差 1 ms | 床超過（実在側が床を 5 ms 超える） | 床なし（実在 37 ms・非実在 19 ms） |
| --- | ---: | ---: | ---: | ---: |
| 20 µs | 100.00% | 100.00% | 100.00% | 100.00% |
| 100 µs | 99.72% | 100.00% | 100.00% | 100.00% |
| 300 µs | 29.38% | 100.00% | 100.00% | 100.00% |
| 1 ms | 2.40% | 99.75% | 100.00% | 100.00% |

ADR-0113 §検討した選択肢 の計画側の試算（系統差 0 で 0.6〜1.1%、差 0.1 ms は sd 100 µs / 300 µs で 99.8% / 27.8%、差 1 ms は sd 300 µs / 1 ms で 100% / 99.8%）と一致する。
決定 5 の「揺れ 300 µs のとき差 0.1 ms は 7 割強を見逃し、差 1 ms は検出する」も再現した。

### 参考: p 値の計算法による系統差 0 の不合格率（試作。3 列は互いに同じ seed で、上の ① とは別の seed）

| 揺れ sd | 正確法（採用） | 正規近似 | 正規近似＋連続性補正 |
| --- | ---: | ---: | ---: |
| 20 µs | 1.01% | 0.94% | 0.90% |
| 100 µs | 1.13% | 1.06% | 1.02% |
| 300 µs | 1.12% | 1.05% | 0.97% |
| 1 ms | 0.96% | 0.92% | 0.88% |

率としては大差ない。**正確法を採った理由は境界の固定である**: 片側 24・同順位なしの有意水準 1% の境界は W = 464（正確 p ≈ 0.00997）／465（p ≈ 0.0106）で、
正規近似は 464 を p ≈ 0.0106 として棄却しない。計算は片側 24 で 1 ms 程度（分布はキャッシュ）。

### 総当たりの検証

同順位を含む小標本（m, n ∈ 2..7、値の種類を 2〜9 に絞る）400 例で、全組合せを数えた p 値と試作の p 値が 1e-12 以内で一致（自己試験にも 60 例を固定）。

<details><summary>合成試験のスクリプト（node 22。<code>scripts/check-password-reset-mail.js</code> を require する。コミットしない）</summary>

```js
'use strict';
const path = require('path');
const m = require(path.resolve(process.argv[2])); // scripts/check-password-reset-mail.js
const RUNS = 20000; const FLOOR = 150; const V = m.TIMING_VERDICT;
function rng(seed) {
  let a = seed >>> 0;
  return () => { a = (a + 0x6D2B79F5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}
function normal(r) { let u = 0; while (u === 0) u = r(); return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * r()); }
const ns = (ms) => Math.round(ms * 1e6) / 1e6;
const NOISE = {
  halfnormal: (r, sd) => Math.abs(normal(r)) * sd,
  exponential: (r, sd) => -Math.log(1 - r()) * sd,
  lognormal: (r, sd) => sd * Math.exp(0.8 * normal(r)),
  outliers5pct: (r, sd) => Math.abs(normal(r)) * sd + (r() < 0.05 ? 20 * sd : 0),
};
function scenario({ seed, sd, diff = 0, noise = 'halfnormal', drift = 0, existingBase = FLOOR, absentBase = FLOOR }) {
  const r = rng(seed); const f = NOISE[noise];
  const tally = { FAIL: 0, INCONCLUSIVE: 0 }; const ps = [];
  for (let k = 0; k < RUNS; k += 1) {
    const repetitions = [];
    for (let rep = 0; rep < m.TIMING_REPETITIONS; rep += 1) {
      const existing = []; const absent = [];
      for (let i = 0; i < m.TIMING_SAMPLES_PER_SIDE; i += 1) {
        existing.push(ns(existingBase + diff + f(r, sd) + 2 * i * drift));
        absent.push(ns(absentBase + f(r, sd) + (2 * i + 1) * drift));
      }
      repetitions.push({ existing, absent });
    }
    const res = m.evaluateTimingConsistency({ repetitions });
    if (res.verdict === V.FAIL) tally.FAIL += 1;
    if (res.verdict === V.INCONCLUSIVE) tally.INCONCLUSIVE += 1;
    ps.push(res.rankSum.p);
  }
  return { tally, ps };
}
const pct = (x) => `${((100 * x) / RUNS).toFixed(2)}%`;
const SDS = [0.02, 0.1, 0.3, 1]; let seed = 5000;
for (const sd of SDS) { const o = scenario({ seed: seed += 1, sd }); const q = (t) => pct(o.ps.filter((p) => p < t).length);
  console.log(`① sd=${sd} FAIL=${pct(o.tally.FAIL)} INC=${pct(o.tally.INCONCLUSIVE)} p<.05=${q(0.05)} p<.1=${q(0.1)} p<.5=${q(0.5)}`); }
for (const noise of ['exponential', 'lognormal', 'outliers5pct']) console.log(`① ${noise} FAIL=${pct(scenario({ seed: seed += 1, sd: 0.3, noise }).tally.FAIL)}`);
console.log(`① drift FAIL=${pct(scenario({ seed: seed += 1, sd: 0.3, drift: 0.02 }).tally.FAIL)}`);
for (const sd of SDS) {
  const a = scenario({ seed: seed += 1, sd, diff: 0.1 }); const b = scenario({ seed: seed += 1, sd, diff: 1 });
  const c = scenario({ seed: seed += 1, sd, diff: 5 }); const d = scenario({ seed: seed += 1, sd, existingBase: 37, absentBase: 19 });
  console.log(`② sd=${sd} 0.1ms=${pct(a.tally.FAIL)} 1ms=${pct(b.tally.FAIL)} floor+5ms=${pct(c.tally.FAIL)} nofloor=${pct(d.tally.FAIL)}`);
}
```

</details>

## 自己試験と変異試験

- `node scripts/check-password-reset-mail.js --self-test` → **54 件**（#1546 の後の 56 件から、段 1・段 2・分解能 1 ns の境界の 12 件を撤去し、順位和検定の単体 3・判定 5・計画の値 1・時計 1 の 10 件を追加。陽性対照・暖機・標本数・走査件数の 4 件は書き直し）。
- 変異 17 種を `scripts/rs-mutant.js`（scratch。実行後に削除）へ書いて `--self-test` を回し、**17/17 が落ちた**:
  有意水準 5% / 0.1%・片側 6・比較を α × 1.1 / × 0.9・片側検定・正規近似・同順位を最小順位・最後の反復だけ・暖機を含める・段 2 の復活・
  評価不能を先に返す・自己対照を見ない・時計を `Date.now()`・分解能 1 ms・格子の前提を外す・キャッシュ鍵から同順位を落とす。
  `<` と `<=` の違いは p がちょうど 0.01 になる入力が無いため区別できない（計画の「0.01 未満」のとおり `<`）。

## CI 実測の環流の雛形（ADR-0113 フォローアップ 2。integration-stack が検査器の段へ届いたら起票）

届き次第、`/plan-feedback` で次の形で起票する（`feedback.yml` の欄に対応）。**起票前に同件の既存 issue を検索する。**

- **タイトル**: `[feedback] ADR-0113 の順位和検定の CI 実測 —— T-25 の不合格の頻度と p 値の分布（integration-stack N 回）`
- **起点となる計画書の ID**: ADR-0113, ADR-0097, SC-15, NFR-13
- **種別**: 実測の報告（裁定を求めない。ずれがあれば「新たな制約」へ切り替える）
- **集計の母集合**: integration-stack の run（期間・run ID の範囲・件数 N）。**検査器の段まで届いた run だけを数え、届かなかった run の数と理由を別に書く**（0 件を「赤が無い」と読まない）
- **集める値**（各 run の `[check-password-reset-mail] T-25 所要時間` の告知から。`順位和検定 … p=` の行と反復ごとの行）:

  | 項目 | 値 |
  | --- | --- |
  | 検査器の段まで届いた run 数 N ／届かなかった run 数 | |
  | `不合格` の回数（うち p < 0.001 の回数） | |
  | `評価不能` の回数（自己対照の最大値） | |
  | p 値の分布（p < 0.01・0.05・0.1・0.5 の割合、最小値・中央値） | |
  | 反復ごとの中央値の範囲（実在・非実在）と自己対照の範囲 | |
  | 同順位ありの run 数（時計の実効の粒度の確認） | |
  | 床の構成（`RESET_FLOOR`・Istio の有無） | |

- **判断の目安（本 PR の合成試験との照合）**: 系統差 0 なら不合格は約 1%・p はほぼ一様（p < 0.5 が約半数）。
  **p が 0 付近に偏る**、または**不合格が連続する**なら、床の回帰（ADR-0097 決定 3 の契機）を疑う。**p が 1 付近に偏る**なら測定が差を消している（例: 床より長い共通の待ち）ことを疑う。
- **実装側の根拠**: MSP の本 PR ／ `.ai-context/adr/IADR-0470_reset-timing-rank-sum-exact-test.md` ／ 本仕様書 §合成試験

## 受け入れ基準（#1541）

- [x] 判定式が順位和検定で、系統差 0 の合成試験で不合格率 ≈ 1%（上表 0.95〜1.13%。PR に貼る）
- [x] 既知の系統差の検出率の表（上表。PR に貼る）
- [ ] integration-stack で T-25 が偶然の赤を出さない（直近 N 回の run で確認）—— **未達**。integration-stack は検査器の段まで届いていない（#1541 起票時点）。
  判定式の性質上「偶然の赤を出さない」は達成できず（約 1% は出る）、確認できるのは「頻度が約 1% で p が一様」である。上の雛形で環流する

## 残るもの

- 🔴 **系統差が無くても約 100 回に 1 回は `不合格` になる**（ADR-0113 §結果）。文言・文書に見分け方を書いた。
- 🔴 **CI の実測はまだ無い**（フォローアップ 2）。
- 実在側の申請は 1 回の実行で 36 件（メール 36 通）。捕捉用 MTA の保持件数に対して問題の無い量だが、実行時間は反復の分だけ延びる（1 申請 ≈ 床 150 ms ＋ 往復）。
