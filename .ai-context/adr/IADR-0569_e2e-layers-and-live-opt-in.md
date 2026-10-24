---
title: IADR-0569 E2E の実行層を L1/L2/L3/L3-T/L3-L に分け、タグ・Playwright project 名と live の opt-in（E2E_LIVE / E2E_LAYER）を確定する
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0031, ADR-0032, ADR-0090, IADR-0330, IADR-0337, IADR-0440, IADR-0442, IADR-0248]
author: claude
created: 2026-10-21
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032（SPA は BFF セッション。トークンを扱わない）
  - planning:projects/microservices-platform/07_adr/ADR-0090（門は依存ごと。全 skip を緑にしない）
related_specs:
  - ../specs/20261021_2014_e2e-standards-and-layers.md
---

# IADR-0569: E2E の実行層と live opt-in（#2014）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-21
- 決定者: claude（#2014。親 #2013 の波 0 = E-00。設計は #2013 コメント 1〜3）

## 起点・関連

- 起点 issue: #2014（親 #2013）。
- 関連する計画書 ID: NFR（無採番）。計画 ADR: ADR-0032（SPA はトークンを扱わない）・ADR-0090（門は依存ごと・全 skip を緑にしない）・ADR-0031（表示言語）。
- 関連する実装判断: [IADR-0330](./IADR-0330_authenticated-browser-e2e-via-bff-stub.md)（L1 は BFF 応答のスタブ）・
  [IADR-0337](./IADR-0337_screen-smoke-widening-and-mutation-target.md)（全画面スモーク）・
  [IADR-0440](./IADR-0440_a11y-machine-checks-jsx-a11y-and-axe.md) / [IADR-0442](./IADR-0442_notfound-landmark-and-axe-scan-surfaces.md)（axe）・
  [IADR-0248](./IADR-0248_integration-stack-ci-readiness-gate.md)（稼働環境に依存する検査を必須にしない）。
- 作業仕様書: [20261021_2014_e2e-standards-and-layers](../specs/20261021_2014_e2e-standards-and-layers.md)。
- 人が読む基準: `docs/tests/E2E_STANDARDS.md`（本 IADR の決定の写しを持つ）。

## コンテキストと課題

Playwright の E2E は L1（ビルド済み SPA ＋ `page.route('**/bff/**')`）だけで、稼働中の PoC へ流す経路が無い。
#2013 は MSP と AST の両方で、CI のスタブと稼働中の PoC（読み取り・書き込み・取引影響・LLM 費用）に E2E を広げる。
後続の 30 余りの issue が同じ層・タグ・project 名・環境変数を前提にするため、名前を先に 1 つに決めておかないと、
各 PR が別の綴りを作り、`--grep` や `testIgnore` が黙って 0 件になる。

基準書（#2013 コメント 1）とその運用設計（同コメント 3）には次の食い違いがあった。

1. 基準書（#2013 コメント 1。以下の節番号は原文のもの。docs では §3 → §2.1、§8 → §7、§12 → §11）§3 は live 系 project を `playwright.config.ts` に同居させるが、コメント 3 §2.3 と E-31 の触るファイル領域は
   別ファイル `playwright.live.config.ts` を前提にしている。
2. `E2E_LAYER` が外す対象は `trading` と `llm` だけと書かれているが、基準書 §12 は `rw` も dispatch 入力に置き、
   nightly は setup ＋ ro だけとしている。`rw` の既定が決まっていない。
3. 基準書 §8 は axe のタグ `wcag21a` / `wcag21aa` を「既存（IADR-0442）」と書くが、現行の `a11y.smoke.spec.ts` は
   `wcag2a` / `wcag2aa` / `best-practice` だけで、IADR-0442 決定 3 が足したのも `best-practice` だけである。

## 検討した選択肢

| 論点 | 案 | 採否 |
| --- | --- | --- |
| live の置き場 | A. `playwright.config.ts` に同居し `testIgnore` で外す | 採らない。L1 の `webServer`（プレビュー起動）と `baseURL` が live 実行にも掛かり、project ごとに上書きが要る |
| | **B. `playwright.live.config.ts` を別に置く** | **採る**。L1 の config は live を知らないまま残る |
| 層の選択 | A. タグだけ（`--grep @live-ro`） | 採らない。タグの付け忘れで L1 の実行に live の spec が混ざる |
| | B. project だけ | 採らない。spec の中で層が読めず、`--grep-invert` での除外もできない |
| | **C. project（ファイル名の接尾辞で選ぶ）＋ タグ（spec 内の宣言）** | **採る**。基準書 §3 どおり |
| `E2E_LAYER` | A. `trading` / `llm` だけを外す（`rw` は常に走る） | 採らない。nightly で書き込みが走る |
| | **B. 値域 `ro` `rw` `trading` `llm` を閉じ、既定 `ro`** | **採る** |
| 値域外の値 | A. 無視する | 採らない。綴り違いが 0 件の緑になる（ADR-0090 の「全 skip を緑にしない」と同じ穴） |
| | **B. config の読み込みで失敗させる** | **採る** |

## 決定

### 決定 1 — 実行層は 5 つ

| 層 | 名称 | 書き込み | 起動 | PR の必須チェック |
| --- | --- | --- | --- | --- |
| `L1` | CI スタブ層 | なし（スタブ） | PR・push | **はい**（現状の `frontend.yml` の `e2e` ジョブ） |
| `L2` | live 読み取り層 | 禁止（ログインによるセッション作成だけ許す） | nightly・手動 | いいえ |
| `L3` | live 書き込み層 | 自分が作ったものだけ（`e2e-<runId>-` 接頭辞・afterEach で削除または復元） | 手動・週次 | いいえ |
| `L3-T` | 取引影響層 | 設定の変更と復元。REAL への切替は送らない | 閉場後にのみ手動 | いいえ |
| `L3-L` | LLM 費用層 | LLM 呼び出しを伴う読み取り。1 実行あたりの回数上限つき | 手動・週 1 回まで | いいえ |

- `L3-T` は取引機能を持つ可変ユニット（AST）だけが spec を持つ。MSP の platform・knowledge には置かない。名前は両リポで共有する。
- L2 以降を必須チェックにしない理由は IADR-0248 と同じ（稼働環境に依存する赤で PR を止めない）。

### 決定 2 — タグ

| 種類 | タグ | 規則 |
| --- | --- | --- |
| 層 | `@live-ro`（L2）・`@live-rw`（L3）・`@trading`（L3-T）・`@llm`（L3-L） | live の spec は自分の層のタグをちょうど 1 つ持つ。**L1 は層タグを持たない**（層タグが無いこと＝L1） |
| 優先度 | `@p0` `@p1` `@p2` | 全 spec が 1 つ持つ（網羅表の優先度と一致させる） |
| 観点 | `@a11y`・`@visual` | 任意。`@visual` は project `visual` でだけ流す |

- **層タグとファイル名の接尾辞は一致させる**（`*.ro.spec.ts` ⇔ `@live-ro`、`*.rw.spec.ts` ⇔ `@live-rw`、`*.trading.spec.ts` ⇔ `@trading`、`*.llm.spec.ts` ⇔ `@llm`）。project の選択は接尾辞で、`--grep` はタグで効くため、食い違うと spec が別の層で走る。一致の検査は live 用 config を置く変更（E-31）の受け入れ基準に入れる（PR #2041 の AI レビュー 🟡）。

### 決定 3 — Playwright の config と project 名

| config ファイル | project | 層 | testMatch |
| --- | --- | --- | --- |
| `playwright.config.ts` | `smoke` | L1 | `e2e/**/*.spec.ts`（`e2e/live/**` を `testIgnore`） |
| 同上（採るときだけ） | `visual` | — | `@visual` のタグを持つ test |
| `playwright.live.config.ts` | `live-setup` | L2〜L3-L の前処理 | `e2e/live/*.setup.ts` |
| 同上 | `live-ro` | L2 | `e2e/live/**/*.ro.spec.ts` |
| 同上 | `live-rw` | L3 | `e2e/live/**/*.rw.spec.ts` |
| 同上 | `trading` | L3-T | `e2e/live/**/*.trading.spec.ts` |
| 同上 | `llm` | L3-L | `e2e/live/**/*.llm.spec.ts` |

- live の 4 project は `dependencies: ['live-setup']` を持つ。
- 現行の L1 project 名は `chromium` である。**次に `playwright.config.ts` を触る PR で `smoke` へ改める**（本 PR は docs だけ。
  `chromium` を project 名として参照する箇所は 0 件——`frontend.yml` の `playwright install chromium` はブラウザ名である）。
- ja-JP 以外のロケールの project を足すときは `smoke-<locale 小文字>`（例 `smoke-en-us`）とする。

### 決定 4 — live の opt-in は `E2E_LIVE` と `E2E_LAYER` の 2 つ

- **`E2E_LIVE`**: 値がちょうど `1` のときだけ `playwright.live.config.ts` が project を有効にする。それ以外（未設定を含む）は
  全 project を 0 件にする（`scripts/verify-oidc-edge-flow.sh` の `--live` / `LIVE=1` と同じ思想）。L1 の config はこの変数を読まない。
- **`E2E_LAYER`**: 値域は `ro` / `rw` / `trading` / `llm` のカンマ区切り。未設定は `ro` とみなす。選ばれた層の project だけを有効にし、
  `live-setup` は依存として常に走る。nightly は `ro`、`e2e-live.yml` の `workflow_dispatch` 入力 `layer` をそのまま渡す。
- **値域外の値、および `E2E_LIVE=1` 無しの `E2E_LAYER` は config の読み込みで失敗させる**（exit ≠ 0）。0 件の緑にしない。
- 前提（readiness・閉場判定）が満たされないときは exit 2 の「前提未整備」とし、skip の緑とも失敗の赤とも区別する。
- 接続先・資格情報などその他の `E2E_*` 変数（`E2E_EDGE_URL`・`E2E_KC_URL`・`E2E_RUN_ID`・`E2E_<ROLE>_PASSWORD`・
  `E2E_<ROLE>_TOTP_SECRET`・`E2E_INSECURE_TLS`・`E2E_AUTH_DIR`）は名前だけを予約し、意味と既定値は live 用 config を
  置く変更（#2013 の E-31 / E-33）が決める。

### 決定 5 — axe のタグは現状と目標を分けて書く

- 現状: `wcag2a` / `wcag2aa` / `best-practice`（IADR-0442 決定 3）。現行の test 名の「WCAG 2.1 A/AA」は、
  WCAG 2.1 で加わった達成基準を評価していない。
- 目標: `wcag21a` / `wcag21aa` を足す。足すのは axe を全画面へ広げる変更（#2013 の E-10）で、増えた違反を先に実測し、
  直すか指紋スナップショットへ載せる。本 IADR はタグを変えない。

## 理由

- 名前を後続の 30 余りの PR より先に 1 か所で決めれば、綴りの揺れが検査の空振りになる前に止まる。
- live を別 config にすると、L1 の config・CI ジョブ・必須チェックは本決定で何も変わらない。
- `E2E_LAYER` の値域を閉じて fail させるのは、ADR-0090（全 skip を緑にしない）を config の段で当てはめたものである。

## 影響

- 本 PR で変わるのは文書だけで、`playwright.config.ts`・ワークフロー・必須チェックは変わらない。
- 後続の PR は本 IADR の名前を使う。名前を変えるには本 IADR を改定する。
- AST は本 IADR を正本として参照する（E-01）。AST 固有の決定（L3-T で送らない操作・閉場判定の置き場）は AST 側の IADR が持つ。

## 残余リスク

- `smoke` への改名は後続 PR に残る。それまで `docs/tests/E2E_STANDARDS.md` の表と config の現物は食い違う（同書 §2.1 と §12 に「現状」として書いた）。
  ［2026-10-24 追記 / #2023］改名した（`chromium` → `smoke`）。ロケールの project `smoke-en-us` も本決定の命名で足した。食い違いは解消した。
- `E2E_LAYER` の fail-loud は live 用 config の実装に依存する。E-31 の受け入れ基準で値域外の値が失敗することを確かめる。
