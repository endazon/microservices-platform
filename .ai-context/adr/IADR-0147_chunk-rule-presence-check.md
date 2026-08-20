---
title: IADR-0147 manualChunks の規則欠落は「名前つきチャンクの実在」でしか捕まらない — 予算とラチェットは別の退行を見る
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0031, IADR-0134, IADR-0118, IADR-0141, IADR-0145]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
related_specs:
  - ../specs/20260808_issue-556_chunk-budget-check.md
---

# IADR-0147: manualChunks の規則欠落は「名前つきチャンクの実在」でしか捕まらない

- 状態: Accepted
- 日付: 2026-08-08
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（退行防止）／計画 ADR `ADR-0031`（フロントスタック）
- 関連 issue: [#556](https://github.com/endazon/microservices-platform/issues/556)（起点。親 [#454](https://github.com/endazon/microservices-platform/issues/454)）。出所は **#512 / PR #549 の申し送り 3・変異試験 M6 / M7**
- 関連する実装 ADR: [IADR-0134](./IADR-0134_spa-route-code-splitting-boundaries.md)（分割境界。決定 3 が `manualChunks` の 3 規則を定める）／
  [IADR-0118](./IADR-0118_backend-coverage-floor.md)（床のラチェット）／[IADR-0145](./IADR-0145_landed-subject-check-scope.md) 決定 3（**偽陽性は塞ぎ、検出漏れは開示する**）／[IADR-0141](./IADR-0141_audit-rounds-and-population-drawing.md)（母集合）

## 背景

#512（PR #549）が SPA のバンドルをルート単位に分割し、Vite の 500 kB 警告を解消した。
分割の設計は `vite.config.ts` の `manualChunks` の 3 規則（`vendor-react` / `ui` / `vendor-query`）に依存するが、
**その規則構成を守る機械が無かった** —— #512 の変異試験は、規則を外してもビルドが成功し警告も出ない
（＝素通りする）ことを実測している。

## ★ 決定の根拠となった実測 —— **issue が提案した 3 判定では捕まらない**

issue #556 は判定を 3 種と書いていた: ①1 チャンク 500 kB 超（fail）／②初期ロード合計のラチェット（fail）／
③1 kB 未満の遅延チャンク数の増加（warn）。**着手時に 2 規則それぞれを実ビルドで外して測り直した。**

**走査基準**: `develop` = `96c2dbe`。baseline は初期ロード合計 **577.68 kB** / 最大チャンク **274.46 kB** /
1 kB 未満の遅延チャンク **5 本**（**issue 本文の 577.54 kB・3 本はいずれも古かった**。転記せず引き直した）。

| 変異 | 最大チャンク | 初期ロード合計 | 1 kB 未満 | 当該チャンク |
| --- | --- | --- | --- | --- |
| `ui` 規則を外す | 306.69 kB（**< 500**） | **544.87 kB（減る）** | 5 → 9 | **消える** |
| `vendor-react` 規則を外す | 458.92 kB（**< 500**） | 578.55 kB（**+0.87 kB = +0.15%**） | 5 → 5（**不変**） | **消える** |

- **①はどちらでも発火しない。** 458.92 kB が上限の 91.8% で止まる。
- **②は `ui` では発火しない** —— 規則を外すと初期ロードが**増えるとは限らない**。`ui` の 65 kB は
  index へ吸収される一方、共有モジュールが遅延側へ散ることで**初期分は減る**。
  **「予算を超えたら落とす」は「規則が外れたら落とす」と向きが違う。**
- **②が `vendor-react` で発火するのは +0.15% の紙一重**であり、検出器として当てにできない。
- **③は `vendor-react` では 1 本も動かない。**

**したがって規則の欠落を確実に捕まえるのは「名前つきチャンクが実在するか」だけである。**

## 決定

### 決定 1: 規則の欠落は「**必須チャンクの実在**」で判定する（fail・最優先）

`scripts/chunk-budget-baseline.json` の `requiredChunks` に列挙した名前が、
`dist/assets` に `<name>-<hash>.js` として実在することを検査する。**これが唯一の確実な検出器である。**

issue が挙げた 3 判定は**捨てずに併せ持つ** —— ただし役割を読み替える。
**それぞれ別の退行を見ており、規則の欠落を見ているのではない。**

| 判定 | 何を見るか | 段階 |
| --- | --- | --- |
| 1. 必須チャンクの実在 | **規則構成の欠落** | fail |
| 2. 1 チャンクの上限（500 kB） | 巨大チャンクの再発 | fail |
| 3. 初期ロード合計のラチェット | 初期ロードの肥大 | fail |
| 4. 1 kB 未満の遅延チャンク数 | 往復の増加 | **warn** |

### 決定 2: 判定 4 は warn に留める

往復の増加は**性能の劣化ではあっても壊れではない**。fail にすると、遅延ルートを 1 本足すたびに
無関係な PR が止まる。[IADR-0118](./IADR-0118_backend-coverage-floor.md) の床と同じく、**止めるべきものだけを止める。**

### 決定 3: 初期ロードは `index.html` 由来で数える（CSS は数えない）

エントリの `<script type="module" src>` と `<link rel="modulepreload" href>` の和を初期ロードとする。
「`assets/` の全 JS」にすると遅延チャンクまで数え、**ルート分割を進めるほど床が上がる**という
逆向きの指標になる。**CSS は数えない** —— `manualChunks` は JS の分割規則であり、
CSS を含めると Tailwind の増減で JS の規則検査が揺れる。

### 決定 4: チャンク名の照合は緩い側を採り、**穴を明記する**

Vite のハッシュには `-` が現れる（実測: `CHRHn5b-` / `BY-IZaSY`）。このため
`ui-widgets-extra-<hash>.js` のような**同じ接頭辞を持つ別チャンク**を `ui` の実在と誤認する。
ハッシュ長を 8 に固定すれば分離できる（本リポの実測はすべて 8 文字）が、**採らない** ——
Vite / Rollup がハッシュ長を変えた瞬間に「必須チャンクが無い」という**偽陽性で全 PR を止める**。

**[IADR-0145](./IADR-0145_landed-subject-check-scope.md) 決定 3 および `.claude/rules/traceability.md` の方針
（検出漏れは開示してよいが、偽陽性は塞ぐ）に従う。** 穴はスクリプト冒頭と self-test に明記し、
**self-test は「穴が在ること」を固定する**（将来誰かが塞いだらテストが落ちて気付ける）。

### 決定 5: 床とコードの乖離を self-test で止める

`baseline.requiredChunks` が `vite.config.ts` の `manualChunks` が返す名前と一致することを self-test で突き合わせる。
**規則を足したのに床へ入れ忘れると、新しい規則だけ検査されないまま緑になる** ——
本検査が防ごうとしている状態そのものである。

> **この突き合わせは実際に自分の欠陥を捕まえた。** 最初の実装は `return '<name>'` だけを拾っており、
> **三項演算子で返している `ui`**（`return id.includes('/packages/ui/') ? 'ui' : undefined`）を
> 取りこぼしていた。self-test が無ければ、`ui` を `requiredChunks` から落としたまま緑で通っていた。

### 決定 6: 結線は `frontend.yml` の `build-test`（**`dist` が在るジョブはそこだけ**）

`--require`（成果物が無ければ fail）で結線する。`ci.yml` の `scripts-tests` には `dist` が無いため、
そちらでは self-test のみを走らせる。**「検査器を作った」と「CI で走る」は別である**
（[IADR-0140](./IADR-0140_cross-repo-issue-ref-checker.md) 決定 2 が言う結線の問題）。

`.github/workflows/` の編集には `workflow` スコープを持つ経路からの push が要る（`CLAUDE.md` 末尾）。
**本 PR はローカルの認証で結線まで行った** —— #556 が「AI だけでは完結しない」とされていた根拠は
実行環境固有のものであり、本セッションの環境では成立しない（#617）。

## 検出しないこと

- **チャンクの中身**（どのモジュールがどのチャンクに入ったか）。規則を「外す」ではなく「書き換える」
  変更は、名前が残る限り実在判定を通る。判定 2・3 が量の側から部分的に見るのみである。
- **gzip 後のサイズ**。床は生バイトで持つ（gzip 率は圧縮実装の版で動くため、床としては不安定）。
- **同じ接頭辞を持つ別チャンクとの取り違え**（決定 4）。

> **［2026-08-08 追記 / フェーズ末クロス監査］self-test の潜在的偽陽性を塞いだ。**
>
> 「`baseline の requiredChunks` が `vite.config.ts` の `manualChunks` と一致する」self-test は、
> **region 内の裸の文字列リテラルをすべてチャンク名として拾っていた。**
> 現行の 3 規則は述語が正規表現リテラル（`/^(react|…)\//`）なので発火しないが、
> **述語に文字列を使う規則を 1 本足すと壊れる** ——たとえば
> `if (pkg.startsWith('lodash')) return 'vendor-lodash';` を足すと `lodash` までチャンク名として拾い、
> self-test が落ちる。**指示どおり `requiredChunks` へ `lodash` を入れると、今度は判定 1
> （必須チャンクの実在）が実ビルドで恒久的に fail する** ——`lodash` という名前のチャンクは生成されないためである。
>
> **決定 4 は「偽陽性は必ず塞ぐ」を方針として掲げている**（他人の PR を止めるため）。
> したがって開示ではなく塞ぐ側を採り、**抽出を `return` 句に限定した**
> （チャンク名が現れるのは `return` 句だけであり、述語のリテラルは対象外になる）。
> 三項演算子で返す `ui` は `return` 句の中にあるので従来どおり拾える。

## 影響

- `manualChunks` の規則を増減するときは `chunk-budget-baseline.json` の `requiredChunks` も更新する
  （しないと self-test が落ちる）。
- 初期ロードを意図的に増やすときは `node scripts/check-chunk-budget.js --update` で床を更新し、
  **差分を PR に載せる**（[IADR-0118](./IADR-0118_backend-coverage-floor.md) と同じ運用）。
