---
title: 作業仕様書 — 所要時間の札を T-25 へ揃え、標本を 1 ms より細かい分解能の時計で測る（#1525・planning#650 の裁定・計画 ADR-0108）
type: spec
status: done
related_ids:
  - SC-15
  - NFR-13
  - ADR-0094
  - ADR-0103
  - ADR-0108
  - IADR-0432
  - IADR-0463
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0108_timing-samples-at-microsecond-resolution.md (Accepted 2026-09-26)
  - planning:projects/microservices-platform/07_adr/ADR-0103_degenerate-self-control-is-not-a-bound.md (決定 3 を ADR-0108 が部分改定)
related_specs:
  - 20260925_1470_timing-self-control-step
  - 20260926_1519_plan-adr-range-0110
issue: "#1525"
---

# 作業仕様書 — 所要時間の札を T-25 へ、標本を 1 ms より細かい分解能で

## 起点

- issue: #1525 ／ 環流 planning#650 → 裁定（2026-09-26、planning#653 でマージ）→ 計画 ADR-0108（Accepted）
- 前提: #1519（計画 ADR レンジを 0110 へ。ADR-0108 をコミット件名で引くため。本 PR はその上に積む）

### 計画の裁定（要点。計画書は読み取り専用の隣接クローンの `origin/main` = `244e63c` で読んだ）

| # | 裁定 / 決定 | 実装での扱い |
| --- | --- | --- |
| (a) | 所要時間の項目 ID は **T-25**（実装のテスト仕様書に合わせる） | 検査器の札 `[T-10][所要時間]` / `[T-10][評価不能]` → `[T-25]…`。`integration-stack.yml` の「T-10 の所要時間」コメントも |
| ADR-0108 決定 1 | 標本を **1 ms より細かい分解能の時計**で測る。刻みは ADR-0103 決定 1 のとおり実装が導く。**分解能の宣言と、それを固定する検査も新しい時計に合わせる** | `process.hrtime.bigint()`（整数 ns）。`TIMING_SAMPLE_RESOLUTION_MS = 1 / NS_PER_MS`。宣言と時計の一致を自己試験と実行時の前提で固定 |
| ADR-0108 決定 2 | 見逃しは「CI の門の分解能と標本数の下限未満」として受け入れる。旧根拠「同じ測定器を使う攻撃者にも見えない」は取り下げ | 旧根拠を書く live 文書（`docs/screens/SC-15`）を改める。検査器の注記も |
| ADR-0108 フォローアップ 1 | `IADR-0432` の後継へ記録する | **`IADR-0463`**（IADR-0432 決定 5 の部分改定）＋ IADR-0432 に日付つき追記 |
| ADR-0108 フォローアップ 2 | 変更後の CI の実測（刻みの値・段 1 と段 2 の比率・不合格の頻度）を環流で届ける | 🔴 **本 PR の時点では実測できない**（下記）。出力に段の内訳を足し、環流の下書きを本仕様書の末尾に置く |

## 設計（詳細は IADR-0463）

1. **時計**: `timingClockNs()` ＝ `process.hrtime.bigint()`、`elapsedMsBetween(a, b)` ＝ `Number(b - a) / NS_PER_MS`。
   `performance.now()` は差が格子に乗らず分解能を宣言できないので採らない（ログイン経路の測定器と同じ時計）。
2. **刻み**: `selfControlStepMs` は変えない。分解能 1 ns・各群 3 標本で刻み 1 ns（計算の帰結）。
3. **段 1 の比較**: 中央値と刻みを「分解能の半分＝1」の整数（`toHalfTicks`）へ直して `<=` で比べる。
   🔴 ms のまま比べると、152,999,004 ns と 152,999,003 ns の差が `1.0000000258969521e-6` になり刻み `1e-6` を超える。
4. **格子の前提**: 宣言した分解能の格子に乗らない標本があれば判定へ進まず不合格（宣言と時計の食い違い）。
5. **標本と分解能は組で渡す**: `evaluateTimingConsistency({ repetitions, resolutionMs })`。省略時は宣言値。
6. **段の内訳**: `perRepetition[].stage` と行「段の内訳: 段 1 で判定 n / 段 2 で判定 m / 段へ進まず k」。

### #1491 の境界試験の扱い（正直な適応）

#1491 が足した段 1 の試験（#1470 の形 1 ms / 0.5 ms / 2 ms / 1.5 ms の境界、広い自己対照）は**整数 ms の標本**である。
新しい既定の分解能（1 ns）で判定すると、差 1 ms は刻みを超えて段 2 へ回り、自己対照 1.00 なら不合格になる ——
**これは新しい時計での正しい挙動であり、試験の期待を書き換えて緑にするのは不正直である。** そこで:

- 旧試験は **`resolutionMs: 1`（`MS_CLOCK`）を明示**して渡す。段 1 の論理（差 ≦ 刻み／刻みを超えれば段 2）は分解能に依らないので、
  整数 ms の時計の標本として意味を保つ。期待値は 1 つも変えていない。
- **同じ #1470 の形を既定の分解能で判定すると段 2 で不合格になる**ことを新しい試験で固定した（宣言が刻みを決めることの固定。
  分解能を 1 ms へ戻す変異はここで落ちる）。
- 新しい時計での境界（差ちょうど 1 ns は合格・1.5 ns は段 2・0.5 ns は合格）は、**時計の読みから作った ms の浮動小数**で撃つ。

## 母集合の引き方（規則 1〜6・9・10）

走査はいずれも `git grep`（追跡下・`src/ai-stock-trading` を除く）。

**軸 1（誤りの側の文字列＝所要時間を T-10 と呼ぶ形。あり得る形を列挙）**:
`git grep -n -E "T-10.{0,12}所要時間|所要時間.{0,20}T-10|T-10\]\[(所要時間|評価不能)|T-10 の\*{0,2}所要"`
＋ テンプレート文字列の札（`[T-10][${TIMING_VERDICT.INCONCLUSIVE}]`）は正規表現に掛からないため、検査器ファイルは `grep -n "T-10"` の生の出力を全行読んだ。

| ヒット | 扱い |
| --- | --- |
| `scripts/check-password-reset-mail.js`（変更前の `T-10` 52 行のうち、所要時間の札 9・注記 4・通知 1・自己試験名 18 の計 32 行） | **対象**。所要時間の軸と対の前提（`evaluateTimingPair`）の札・名前だけを T-25 へ。応答ステータス・本文の比較（`evaluateConcealment` 等）と `makeAbsentUsername` の試験は T-10 のまま |
| `.github/workflows/integration-stack.yml:90`（「T-10 の所要時間を再測定する」） | **対象**（コメントのみ。起動条件・ジョブ名・必須チェックは不変）。🔴 issue 本文と裁定は「コメントは既に T-25」と書くが、それは 236 行目だけで、90 行目は T-10 のままだった |
| `.ai-context/adr/IADR-0432`（131・144・194・241 行） | 除外（本文）。凍結記録。**日付つき追記で IADR-0463 を指す** |
| `.ai-context/adr/IADR-0427`（167・210 行） | 除外。凍結記録（当時の「T-10 へ所要時間の軸を足す」という計画の文言） |
| `.ai-context/specs/` の過去回 5 本 | 除外。確定済みの作業仕様書 |
| `CHANGELOG.md:767` | 除外。生成物（過去の PR タイトル） |
| `scripts/check-login-existence-disclosure.js` の `[T-10]` | 除外。**SC-13（ログイン）のテスト仕様書の T-10**（別の仕様書の別の項目） |

**軸 2（分解能の旧値＝整数 ms・1 ms 刻み・`Date.now()` の差）**:
`git grep -n -E "整数 ?ms|TIMING_SAMPLE_RESOLUTION|刻み[はが]? ?1 ?ms|1 ?ms ?刻み|1 ms 未満|selfControlStep|Date\.now\(\) の差"`

| ヒット | 扱い |
| --- | --- |
| `scripts/check-password-reset-mail.js`（宣言・導出の注記・自己試験の `=== 1`） | **対象** |
| `docs/screens/SC-15_password-reset.md:288` | **対象**（「整数 ms の標本・各群 3 標本では 1 ms」） |
| `docs/tests/SC-15_password-reset.md:61`（T-25 の行） | **対象**（同） |
| `.ai-context/adr/IADR-0432:198,210,213,214` | 除外（本文）。追記で現行値でなくなったことを書いた |
| `.ai-context/specs/20260925_1470_timing-self-control-step.md` | 除外。確定済み |

`scripts/check-password-reset-mail.js` の残る `Date.now()`（送出の待ち合わせの期限 `deadline`）は**所要時間の標本ではない**ので除外。

**軸 3（取り下げられた根拠の文言）**: `git grep -n -E "攻撃者にも見え|同じ測定器"`

| ヒット | 扱い |
| --- | --- |
| `docs/screens/SC-15_password-reset.md:293`（「同じ測定器を使う攻撃者にも見えない差であり」） | **対象**。🔴 **issue の反映先に無かった**（引き直しで見つかった） |
| `scripts/check-password-reset-mail.js`（本 PR の注記「旧根拠…は取り下げた」） | 本 PR が書いた取り下げの記述であり、誤りではない |

**軸 4（検査器を名指す文書の件数・説明）**: `git grep -l "check-password-reset-mail"` の 32 ファイル（本 PR の新規 2 本を足す前）のうち live な説明を持つもの ——
`scripts/README.md:44`（T-25 の説明が無く、自己試験の件数 17 件が古い）を**対象**（T-25 を足し 57 件へ）。
`deploy/local/README.md` / `docs/operations/keycloak-smtp-relay-setup-runbook.md` / `scripts/scripts.repo.test.js` は
所要時間の札・分解能に触れていない（`grep -n "所要時間\|T-10\|T-25"` で確認）ので対象外。`.ai-context/` は凍結記録。

**規則 10（この変更で新たに誤りになる自分の記述）**:
- 本 PR の IADR 番号 `IADR-0463` は、push 時点の develop の最大（0460）と開いている PR の新規 IADR（#1513 = 0461・#1524 = 0462）の次。
  **先にマージされた PR が 0463 を取れば改番が要る**（ファイル名・索引・trace ブロック 2 文書・IADR-0432 の追記・本仕様書・検査器の注記は
  IADR 番号を持たない）。コミット件名・PR タイトルには IADR 番号を入れていない。
- `scripts/README.md` の自己試験件数（57 件）は本 PR 時点の実測値。
- #1518 が同じファイル（検査器・テスト仕様書・画面仕様書・IADR-0432・`scripts/README.md`・`integration-stack.yml`）に触れている。
  後からマージされる側が rebase で解消する。

## 受け入れ基準

- [x] 所要時間の失敗の札が `[T-25][所要時間]` / `[T-25][評価不能]`。`[T-10]` は応答ステータス・本文の比較だけに残る
- [x] `submitResetRequest` は `timingClockNs()`（`process.hrtime.bigint()`）で測り、`Date.now()` / `performance.now()` を使わない（自己試験がソースで固定）
- [x] 分解能の宣言は時計の単位から導かれ（`1 / NS_PER_MS`）、**実際の時計**の連続する 2 読みの差が 1 ms 未満で格子に乗ることを自己試験が確かめる
- [x] `selfControlStepMs` は変えず、既定の分解能・各群 3 標本で 1 ns を返す
- [x] 段 1 の境界は半格子の整数で比べ、ちょうど刻みの差（ms の浮動小数では刻みを超えて見える値）が合格する
- [x] #1491 の境界試験は `resolutionMs: 1` を明示して期待値を変えずに通り、同じ形を既定の分解能で判定すると段 2 で不合格になることも固定した
- [x] 格子に乗らない標本は判定へ進まず不合格
- [x] 段 1 ／段 2 で判定した反復の数を出す
- [x] 変異 18 種がすべて自己試験で落ちる
- [x] `IADR-0463` に記録し、`IADR-0432` に日付つき追記
- [x] `node scripts/scripts.test.js` と文書検査が緑

## 検証（実行コマンドと結果）

🔴 **検査器は `--self-test` でしか走らせていない**（引数なしで走らせると稼働クラスタの Keycloak へ実際に申請を投げる）。

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-password-reset-mail.js --self-test` | 57 件 OK（従前 50 件） |
| 変異試験（scratch の `t25-mutate.js`。検査器を変異させた一時コピーに `--self-test` だけを走らせる） | 18 種すべて killed（`survived=0/18`） |

（その他の検査の結果は PR 本文に載せる。）

## 🔴 CI での実測は本 PR の時点では取れない —— 環流は合成試験の結果で出す

ADR-0108 フォローアップ 2 は、変更後の CI の実測（刻みの値・段 1 と段 2 の比率・不合格の頻度）を環流で届けるよう求めている。
**integration-stack は現在この検査器の段まで到達していない**（オブジェクトストレージの差し替え #1513 → その後に Qdrant のコレクションの問題）。
**「測れた」とは書かない。** 代わりに、監査（#1526 の監査・GO-with-nits）が合成試験で示した「揺れだけで不合格が常態化する」ことを
自分で再現し、その数値で環流する（2026-09-26 のコーディネータの指示）。CI の実測は測れた時点で追って届ける。

### 合成試験（再現手順と結果）

本リポジトリに解析用スクリプトの置き場は無い（`scripts/` は検査器と試験、`docs/` は文書）ため、スクリプトはコミットせず本節に全文を置く。
**検査器の判定関数 `evaluateTimingConsistency` をそのまま呼ぶ**（稼働クラスタには触れない）。

- 条件: 床 150 ms。各標本 ＝ 150 ms ＋ |N(0, sd)|（半正規）。**実在・非実在とも同じ分布から独立に取る**（系統差 0）。
  反復 3（1 回目は暖機として捨てる）× 片側 6 標本（検査器の定数そのもの）。sd ＝ 20 µs / 100 µs / 300 µs / 1 ms。各 5,000 実行。擬似乱数は seed 固定（mulberry32・seed 1000〜1003）。
- 新（本 PR）: 標本を整数 ns へ丸め、分解能 1e-6 ms で判定。
- 旧（`Date.now()` 相当）: 開始の位相を U[0, 1) ms で取り、`floor(開始 ＋ 所要) − floor(開始)` の整数 ms、分解能 1 ms で判定。

| sd | 時計 | 合格 | 不合格 | 評価不能 | 段 1 で判定した反復 | 段 2 で判定した反復 | 段 2 で cross > self の反復 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 20 µs | 整数 ns（新） | 36.6% | 63.4% | 0.0% | 0.0% | 100.0% | 39.1% |
| 20 µs | 整数 ms（旧・Date.now 相当） | 100.0% | 0.0% | 0.0% | 100.0% | 0.0% | 0.0% |
| 100 µs | 整数 ns（新） | 38.0% | 62.0% | 0.0% | 0.0% | 100.0% | 38.5% |
| 100 µs | 整数 ms（旧・Date.now 相当） | 100.0% | 0.0% | 0.0% | 100.0% | 0.0% | 0.0% |
| 300 µs | 整数 ns（新） | 37.6% | 62.4% | 0.0% | 0.0% | 100.0% | 38.8% |
| 300 µs | 整数 ms（旧・Date.now 相当） | 100.0% | 0.0% | 0.0% | 100.0% | 0.0% | 0.0% |
| 1000 µs | 整数 ns（新） | 38.0% | 62.0% | 0.0% | 0.0% | 100.0% | 38.2% |
| 1000 µs | 整数 ms（旧・Date.now 相当） | 97.1% | 2.9% | 0.0% | 98.4% | 1.6% | 1.5% |

読み方:

- **段 1 は実質的に消えた**（新では判定に使った反復の 0.0% が段 1）。
- **段 2 の `cross > self` は系統差 0 でも反復の約 38〜39% で成り立つ。** 自己対照は実在側を 2 群に分けた **3 標本の中央値**どうしの比、
  比は **6 標本の中央値**どうしの比であり、差が無くても比が自己対照を上回る確率が 0.5 に近い形で残る。
  判定に使う反復は 2 回で、どちらかが落ちれば不合格なので、実行単位では 1 − (1 − 0.385)² ≈ 62% になる。**揺れの大きさ（sd）にほぼ依らない**（比の尺度不変性）。
- **`評価不能` は 0 件。** 自己対照 ≧ 2 倍の境界には床の内側の揺れでは届かない —— 不合格は `評価不能` ではなく `不合格` として出る。
- 監査の数値（新 ≈ 62%・旧 0〜0.3%）と一致する。旧の sd 1 ms で本試算が 2.9% と高いのは、`Date.now()` の位相を一様に取った模型の違いによる
  （監査の模型は未確認）。いずれにせよ新旧の差は 1 桁以上である。

<details><summary>スクリプト全文（node 22 で実行。`scripts/check-password-reset-mail.js` を require する）</summary>

```js
'use strict';
const path = require('path');
const m = require(path.join('<リポジトリのルート>', 'scripts/check-password-reset-mail.js'));
const { evaluateTimingConsistency, TIMING_REPETITIONS, TIMING_SAMPLES_PER_SIDE, TIMING_VERDICT } = m;
function rng(seed) {
  let a = seed >>> 0;
  return () => { a = (a + 0x6D2B79F5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}
function normal(r) { let u = 0; while (u === 0) u = r(); return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * r()); }
const FLOOR_MS = 150; const RUNS = 5000; const SDS_MS = [0.02, 0.1, 0.3, 1];
function durationMs(r, sd) { return FLOOR_MS + Math.abs(normal(r)) * sd; }
function nsSample(r, sd) { return Math.round(durationMs(r, sd) * 1e6) / 1e6; }
function msSample(r, sd) { const start = r(); return Math.floor(start + durationMs(r, sd)) - Math.floor(start); }
function run(sampler, resolutionMs, sd, seed) {
  const r = rng(seed);
  const tally = { [TIMING_VERDICT.PASS]: 0, [TIMING_VERDICT.FAIL]: 0, [TIMING_VERDICT.INCONCLUSIVE]: 0 };
  const stage = { 1: 0, 2: 0, other: 0 }; let failedReps = 0; let judgedReps = 0;
  for (let k = 0; k < RUNS; k += 1) {
    const repetitions = [];
    for (let rep = 0; rep < TIMING_REPETITIONS; rep += 1) {
      const existing = []; const absent = [];
      for (let i = 0; i < TIMING_SAMPLES_PER_SIDE; i += 1) { existing.push(sampler(r, sd)); absent.push(sampler(r, sd)); }
      repetitions.push({ existing, absent });
    }
    const res = evaluateTimingConsistency({ repetitions, resolutionMs });
    tally[res.verdict] += 1;
    for (const p of res.perRepetition.filter((x) => !x.warmup)) {
      judgedReps += 1;
      if (p.stage === 1) stage[1] += 1; else if (p.stage === 2) stage[2] += 1; else stage.other += 1;
      if (p.stage === 2 && p.cross > p.self) failedReps += 1;
    }
  }
  return { tally, stage, failedReps, judgedReps };
}
const pct = (x, n) => `${((100 * x) / n).toFixed(1)}%`;
SDS_MS.forEach((sd, i) => {
  for (const [name, sampler, res] of [['整数 ns（新）', nsSample, 1e-6], ['整数 ms（旧・Date.now 相当）', msSample, 1]]) {
    const o = run(sampler, res, sd, 1000 + i); const t = o.tally;
    console.log(`| ${sd * 1000} µs | ${name} | ${pct(t[TIMING_VERDICT.PASS], RUNS)} | ${pct(t[TIMING_VERDICT.FAIL], RUNS)} | ${pct(t[TIMING_VERDICT.INCONCLUSIVE], RUNS)}`
      + ` | ${pct(o.stage[1], o.judgedReps)} | ${pct(o.stage[2], o.judgedReps)} | ${pct(o.failedReps, o.judgedReps)} |`);
  }
});
```

</details>

### 環流の下書き（確定。起票はコーディネータが行う。`feedback.yml` の欄に対応）

- **タイトル**: `[feedback] ADR-0108 の時計の変更で T-25 が揺れだけで約 62% 不合格になる —— 段 2 の比較（3 標本 vs 6 標本の中央値）の再裁定を求める`
- **フィードバック元（実装リポジトリ）**: microservices-platform
- **実装側の根拠**: MSP#1526（ADR-0108 決定 1 の実装）／ `.ai-context/specs/20260926_1525_timing-resolution-t25.md` §合成試験 ／ `.ai-context/adr/IADR-0463_reset-timing-samples-hrtime-ns-and-half-tick-comparison.md`
- **起点となる計画書の ID**: ADR-0108, ADR-0103, ADR-0094, SC-15, NFR-13
- **種別**: 新たな制約(ADR要)
- **現状（As-Is）**:
  - ADR-0108 決定 1 は標本を 1 ms より細かい分解能の時計で測るとし、刻みは ADR-0103 決定 1 のとおり実装が導くとした。§結果 は「段 2 へ回る反復が増え、揺れによる不合格が増える可能性がある。その場合は ADR-0103 決定 2 の `評価不能` の規定がそのまま効く」とし、フォローアップ 2 で CI の実測を求めている。
  - 判定式は ADR-0094 決定 1（反復した中央値の比が自己対照を超えない）と ADR-0103 決定 1（中央値の差が自己対照の刻み以下なら合格）の 2 段。自己対照は実在側 6 標本を交互に 2 群（各 3 標本）へ分けた中央値の比、比は実在・非実在各 6 標本の中央値の比。反復 3（1 回目は暖機）× 片側 6 標本。
- **問題点 / あるべき姿（To-Be）**:
  - 🔴 **時計を整数 ns に替えると、系統差 0 の環境でも T-25 は実行の約 62% で `不合格` になる。** 合成試験（検査器の判定関数をそのまま使用）: 床 150 ms、各標本 ＝ 150 ms ＋ |N(0, sd)|、実在・非実在とも同じ分布から独立、反復 3（暖機 1）× 片側 6、sd ＝ 20 µs / 100 µs / 300 µs / 1 ms、各 5,000 実行・seed 固定。
    - 新（整数 ns・分解能 1 ns）: 不合格 **63.4% / 62.0% / 62.4% / 62.0%**、`評価不能` **0%**、段 1 で判定した反復 **0.0%**、段 2 で `cross > self` の反復 **38〜39%**。
    - 旧（`Date.now()` 相当・整数 ms）: 不合格 **0.0% / 0.0% / 0.0% / 2.9%**（段 1 で判定した反復 98〜100%）。
    - 独立の監査の試算（同条件・各 5,000 実行）も新 ≈ 62%・旧 0〜0.3% で一致した。
  - **原因は比較の構造である。** 段 1（刻み 1 ns）は実質的に消え、ほぼ全反復が段 2 で判定される。段 2 の `cross > self` は、上限の自己対照が **3 標本**の中央値どうしの比、測る量の比が **6 標本**の中央値どうしの比であるため、差が無くても反復の約 38〜39% で成り立つ。判定に使う反復 2 回のどちらかが落ちれば不合格なので、実行単位では約 62%。揺れの大きさにほぼ依らない（比の尺度不変性）。
  - 🔴 **ADR-0108 §結果 の「`評価不能` の規定がそのまま効く」は成り立たない。** 床の内側の揺れでは自己対照は 1.00 倍台に留まり、`評価不能` の境界（2 倍）に届かない。揺れによる失敗は `評価不能` ではなく `不合格` として出る（試験で `評価不能` 0 件）。
  - ADR-0103 が扱った「潰れた自己対照」（整数 ms の刻みで自己対照が 1.00 に張り付く）は時計の変更で消えたが、**その下に隠れていた「上限と測る量の標本数の非対称」が表に出た**。旧時計では段 1（刻み 1 ms）がこの非対称を覆っていた。
  - **あるべき姿**: 系統差 0 の環境で門が常時赤にならないこと（赤が常態化すれば本物の退行が埋もれる。ADR-0103 §理由 と同じ論点）。そのうえで ADR-0108 決定 2 の「検出できる下限は分解能と標本数で決まる」を保つ判定式。
- **実装で判明した経緯**: ADR-0108 決定 1 の実装（MSP#1526）の監査で、独立の監査エージェントが合成試験（40,000 ケースで判定が緩くなった例 0・厳しくなった例 16,703）と上記の揺れのみの試験を行い、不合格の常態化を指摘した。実装側で同じ条件を再現して確かめた。integration-stack は現在この検査器の段まで到達していないため CI の実測はまだ無い（到達しだい追って届ける）。
- **提案（計画への反映案）**: ADR-0108（または ADR-0103 / ADR-0094 決定 1）を部分改定する新 ADR で、判定式を再裁定してほしい。候補:
  1. **標本数を増やす**（片側・反復とも）。ただし上限（3 標本の中央値の比）と測る量（6 標本）の非対称は残るため、揺れのみの不合格率は下がりにくい見込み（未試算）。
  2. **中央値の比較をやめ、統計的検定へ替える**（例: 実在・非実在の順位和検定・並べ替え検定を有意水準つきで。自己対照の代わりに帰無分布を使う）。偽陽性率を明示の値（例: 反復あたり 1%）で持てる。
  3. **刻みを時計の目ではなく「雑音の床」から取る**（例: 自己対照の群の中央値の差の分布、または実在側の標本の散らばりから段 1 の幅を導く）。ADR-0103 決定 1 の「刻み＝自己対照の中央値が取り得る最小の間隔」を「自己対照が区別できる最小の差」へ読み替える形。
  4. **上限と測る量の標本数を揃える**（自己対照を実在側 12 標本の 2 群 6 標本で取る、または比を 3 標本どうしで取る）。
  5. **現状（夜間の赤）を受け入れる**（integration-stack は必須チェックではない）。ただし ADR-0097 決定 1 の注記解除の根拠（CI の継続合格）と両立しない。
  - 実装側は裁定まで判定を変えない。採る案が決まれば、同じ合成試験で揺れのみの不合格率と既知の系統差（例: 0.1 ms / 1 ms / 床超過）の検出率を示して実装する。
- **影響範囲**: `scripts/check-password-reset-mail.js` の T-25（リセット申請の所要時間）。ログイン経路は判定しない（ADR-0094 決定 4）。ADR-0097 決定 1（注記解除の根拠＝CI の継続合格）と決定 3（検査器の赤を床の引き直しの契機とする）の運用に直結する —— 揺れだけの赤が床の引き直しの契機を空打ちさせる。

## ［2026-09-26 追記 / #1525］develop の取り込み（#1522 のスカッシュ・#1518 の床の既定 ON）

#1522 のマージ後、base を develop へ付け替え、origin/develop をマージコミットで取り込んだ（rebase・force push はしない）。
衝突は 4 ファイル —— `docs/tests/SC-15`・`docs/screens/SC-15` の trace ブロックは両側の和、`IADR-0432` は #1500 の追記の後に
本 PR の追記を置き Superseded by 行を 3 項目にまとめ、`integration-stack.yml` は #1518 の形（`RESET_FLOOR` を job env で与えない）を採った。

**規則 10（取り込みで新たに入った誤り）**: 軸 1 を取り込み後の木で引き直した。#1518 は**所要時間を T-10 と呼ぶ記述を新たに 3 か所**持ち込んでいた ——

| ヒット | 扱い |
| --- | --- |
| `.github/workflows/integration-stack.yml`（「T-10 の所要時間が赤になって気付ける」「integration-stack で T-10 が継続して合格する」） | **対象**。T-25 へ（衝突解消の中で直した。コメントのみ） |
| `deploy/mail-relay/reset-floor/reset-floor.yaml:34`（「T-10 を測り続ける」） | **対象**。T-25 へ（マニフェストのコメント。描画は不変） |
| `.ai-context/adr/IADR-0432` の #1500 追記（「T-10 の所要時間」「T-10 が継続して合格する」） | 除外（本文）。本 PR の追記に「T-25 のことである」と 1 文足した |

## ［2026-09-26 追記 / #1525］監査の指摘（GO-with-nits）への対応

- IADR-0463 §結果 の「増えた場合は `評価不能` の規定がそのまま効く」は誤りだった —— 揺れによる失敗は自己対照が狭いまま `不合格` になる（合成試験で `評価不能` 0 件）。§結果 を訂正した。
- `integration-stack.yml` の検査器の実体の列挙（T-10 / T-16 / T-17）へ T-25 を足した。
- 環流の下書きを CI の実測を待たず合成試験の結果で確定した（上の節）。
