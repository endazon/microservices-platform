---
title: ユニット直下と雛形の空枠 23 件を撤去し、「.gitkeep のみのディレクトリが無いこと」を 1 述語で機械検査する（issue #1195）
type: spec
status: draft
created: 2026-09-03
updated: 2026-09-03
author: claude
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0069_frontend-scaffolding-frames-and-absence-semantics.md (Accepted 2026-09-02)
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30。決定 4)
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md (§ディレクトリ構成)
related_ids:
  - NFR
  - ADR-0031
  - ADR-0065
  - ADR-0069
  - ADR-0077
  - IADR-0218
  - IADR-0309
  - IADR-0321
  - IADR-0325
  - IADR-0333
  - IADR-0361
---

# 仕様書: 空枠 23 件の撤去と「`.gitkeep` のみのディレクトリが無いこと」の機械検査

## 起点となる計画書（トレーサビリティ）

- 非機能要求（NFR）: 保守性・文書統制（撤回済み規範の残置を機械で止める）
- 計画 ADR: `ADR-0069`「フロントエンドにも空枠を置かない。不在は『関心が無い』と『置き場所が違う』を
  区別する」（Accepted 2026-09-02。環流 planning#510 ／ 反映 planning#517）。
  ほかに `ADR-0065` 決定 4（`.gitkeep` 枠置き規範の撤回）・`ADR-0031`
- 実装 ADR: `IADR-0321`（feature 内部 30 件の撤去。決定 4 で「機械検査は追加しない」）／
  `IADR-0325`（ユニット直下は裁定まで残す。決定 5 で同じく「機械検査は追加しない」）／
  `IADR-0218`（`Superseded`。フロント側に検査器が無いことを記録した）／`IADR-0333`（描画しないモジュールの置き場）
- 本 PR で起こす実装 ADR: `IADR-0361`

## 1. 裁定の確認 —— `IADR-0325` が委ねた問いの答えは「消す」である

`IADR-0325` 決定 1 は **「ユニット直下（`src/` 最上位）と雛形の枠は裁定まで残す」** とし、
撤去の可否を planning#510 へ委ねた。**その裁定が `ADR-0069` として下りた。**

ADR 本文で確かめた条文（要旨。転記ではなく該当節を読んだ結果）:

| `ADR-0069` の箇所 | 内容 |
| --- | --- |
| 決定 1 | **`.gitkeep` のみのディレクトリを置かない。射程は feature 内部・ユニット直下・雛形の 3 者すべて。** `IADR-0325` が残した 23 件も撤去してよい。**雛形と実装ユニットは同時に動かす** |
| 決定 1（射程外） | `docs/` の 4 件は**文書種別の出力先**であり射程外。`IADR-0325` 決定 2 を**追認**する |
| 決定 2 | `IADR-0325` 決定 1 が根拠にした「planning#445 はどちらの側も支えない」を**否定**する。列挙は**非適合の実測**であって必須項目の一覧ではない |
| 決定 3 | 不在には **(a) 関心が無い（適合）／ (b) 関心はあるが置き場所が違う（非適合）** の 2 型がある。枠は (b) を直さず「揃っている」ように見せるだけ |
| 決定 4 | 共有層の区分は「唯一の置き場」ではない。関心に閉じた共有物はその関心の隣に置いてよい |
| 決定 5 | **「`.gitkeep` のみのディレクトリが無いこと」を機械検査に載せる。述語は 1 つだけ。**射程外は理由つきの除外リストで持つ |

したがって本 PR は **`IADR-0325` 決定 1 を置き換える**（`IADR-0325` 本文は書き換えない。
後続 IADR で置き換えを記録する —— `.claude/rules/traceability.repo.md` §Superseded の書式）。
**`IADR-0325` 決定 2（`docs/` の 4 件は残す）は生かす。**

## 2. 母集合（自分で全数走査した。転記ではない）

基点: `origin/develop` を取り込んだ本ブランチ。
`git rev-parse --is-shallow-repository` = **`false`**（出力を出典に使える）。

```console
$ git ls-files "*.gitkeep" | wc -l
27
```

**27 件それぞれについて「同じディレクトリに `.gitkeep` 以外の追跡ファイルがあるか」を判定した**
（同階層の兄弟と、配下の全子孫の両方を数えた。判定スクリプトは本節の表を作るためだけの使い捨てで、
成果物の検査器は §4 のもの）。

| # | ディレクトリ | 同階層の他ファイル | 配下の子孫 | 判定 | 処置 |
| ---: | --- | ---: | ---: | --- | --- |
| 1 | `docs/batch` | 0 | 0 | 空枠 | **射程外・残す**（種別の出力先。`ADR-0069` 決定 1 / `IADR-0325` 決定 2） |
| 2 | `docs/errors` | 0 | 0 | 空枠 | 同上 |
| 3 | `docs/infra` | 0 | 0 | 空枠 | 同上 |
| 4 | `docs/integration` | 0 | 0 | 空枠 | 同上 |
| 5 | `src/knowledge/frontend/src/app` | 0 | 0 | 空枠 | **撤去**（型 (a)。アプリホスト platform が持つ） |
| 6 | `src/knowledge/frontend/src/assets` | 0 | 0 | 空枠 | **撤去**（型 (a)。外部 CDN / Web フォント禁止の帰結） |
| 7 | `src/knowledge/frontend/src/hooks` | 0 | 0 | 空枠 | **撤去**（型 (a)。横断フックは関心の隣） |
| 8 | `src/knowledge/frontend/src/locales` | 0 | 0 | 空枠 | **撤去**（型 (a)。カタログは platform 側。§3 で実測） |
| 9 | `src/knowledge/frontend/src/stores` | 0 | 0 | 空枠 | **撤去**（型 (a)。既定は URL が単一情報源） |
| 10 | `src/knowledge/frontend/src/testing` | 0 | 0 | 空枠 | **撤去**（型 (a)。ハーネスは platform 側） |
| 11 | `src/knowledge/frontend/src/types` | 0 | 0 | 空枠 | **撤去**（型 (a)。表示型は生成 DTO） |
| 12 | `src/knowledge/frontend/src/utils` | 0 | 0 | 空枠 | **撤去**（型 (a)。`lib/echarts/` の判断は `IADR-0333` 決定 2） |
| 13 | `src/platform/frontend/src/assets` | 0 | 0 | 空枠 | **撤去**（型 (a)） |
| 14 | `src/platform/frontend/src/hooks` | 0 | 0 | 空枠 | **撤去**（型 (a)） |
| 15 | `src/platform/frontend/src/stores` | 0 | 0 | 空枠 | **撤去**（型 (a)。唯一の Zustand は `components/ai-chat/`） |
| 16 | `src/platform/frontend/src/types` | 0 | 0 | 空枠 | **撤去**（型 (a)） |
| 17 | `templates/unit-template/frontend/src/app` | 0 | 0 | 空枠 | **撤去**（雛形。決定 1「雛形と実装ユニットは同時に動かす」） |
| 18 | `templates/unit-template/frontend/src/assets` | 0 | 0 | 空枠 | 同上 |
| 19 | `templates/unit-template/frontend/src/components` | 0 | 0 | 空枠 | 同上 |
| 20 | `templates/unit-template/frontend/src/config` | 0 | 0 | 空枠 | 同上 |
| 21 | `templates/unit-template/frontend/src/hooks` | 0 | 0 | 空枠 | 同上 |
| 22 | `templates/unit-template/frontend/src/lib` | 0 | 0 | 空枠 | 同上 |
| 23 | `templates/unit-template/frontend/src/locales` | 0 | 0 | 空枠 | 同上 |
| 24 | `templates/unit-template/frontend/src/stores` | 0 | 0 | 空枠 | 同上 |
| 25 | `templates/unit-template/frontend/src/testing` | 0 | 0 | 空枠 | 同上 |
| 26 | `templates/unit-template/frontend/src/types` | 0 | 0 | 空枠 | 同上 |
| 27 | `templates/unit-template/frontend/src/utils` | 0 | 0 | 空枠 | 同上 |

**撤去 23 件（knowledge 8 / platform 4 / 雛形 11）／残す 4 件（`docs/`）。**
計画側の実測（基点 `d561509`・27 件／射程内 23 件）と一致したが、**これは数え直した結果であって転記ではない。**

**陽性対照**（走査が機能していることの証明。同じ引き方で「空枠でない」ものが出る）:

```console
$ git ls-files "src/platform/frontend/src/utils/"
src/platform/frontend/src/utils/apiErrors.test.ts
src/platform/frontend/src/utils/apiErrors.ts
src/platform/frontend/src/utils/formatDateTime.test.ts
src/platform/frontend/src/utils/formatDateTime.ts          ← 4 件。#1131 で枠が外れた区分
$ git ls-files "templates/unit-template/frontend/src/features/"
… 8 件（index.ts ＋ sample/ 配下 7 件）                     ← `features/` は枠ではない
```

**`.gitkeep` を別名で置いた同型は無い**（`IADR-0325` が軸 2 として引いたのと同じ引き方）:

```console
$ git ls-files "*.keep" "*.placeholder" "*.empty" "*PLACEHOLDER*"
（0 件）
```

## 3. 追随が要る記述の母集合（規則 9。**誤りの側の文字列で全文書を走査してから挙げた**）

```console
$ git grep -n -E "gitkeep|空枠|枠のみ|枠置き|枠だけ" -- docs/ src/ templates/ .claude/ scripts/ .github/
```

ヒット **31 行 / 15 ファイル**。分類:

| ファイル | 行 | 判定 | 理由 |
| --- | --- | --- | --- |
| `src/platform/frontend/README.md` | 30, 39, 57, 59 | **是正する** | ツリーの「中身が無い（.gitkeep のみ）」行と「答えが出るまで消さない」注記。裁定が下りた |
| `templates/unit-template/README.md` | 37–42, 56–57, 70 | **是正する** | `src/` 直下ツリーの `.gitkeep` 11 行と、［2026-08-31 追記 / #1122］の「裁定を待つ」ブロック |
| `docs/how-to/plan-id-range-history-annex.md` | 36 | 除外 | `ADR-0069` のレンジ引き直し記録。**本 PR の結論と同じ側**（追随不要） |
| `docs/tech/tech-requirements.md` | 196 | 除外 | 「`.gitkeep` の枠は撤回」。**撤去する側の記述** |
| `src/README.md` | 74, 82 | 除外 | 同上（2 行とも「撤回された」と書いている） |
| `src/plopfile.js` | 19, 20, 24, 28, 135, 151 | 除外 | 同上（生成器は空枠を作らない） |
| `src/plop-templates/feature/{api,hooks,types}/*.hbs` | 各 1 行 | 除外 | 同上（「この区分を `.gitkeep` の空枠にしない」） |
| `templates/unit-template/frontend/src/features/sample/hooks/useSampleFilter.ts` | 12 | 除外 | 同上 |
| `src/knowledge/frontend/src/features/sc21-ai-suggestions/hooks/useSuggestionFilters.ts` | 12 | 除外 | 同上 |
| `templates/unit-template/backend/Services/SampleService/README.md` | 26–27 | 除外 | 「空のフォルダは作らない」。**撤去する側**（ただし別件で 9 行目を直す。§5） |
| `src/packages/ui/src/components/Card.tsx` | 6 | 除外 | 「区画の枠」＝ UI の語。同音異義 |
| `scripts/check-nul-bytes.js` | 21 | 除外 | 拡張子列挙のコメント。検査器ではない |

**是正 2 ファイル・除外 13 ファイル。** `IADR-0325` 決定 4 が「枠を根拠にした記述のほうが有害である」と
書いたとおり、**枠の撤去とこの 2 件は同じ PR で動かす**（片方だけ直すと「消した」と「消さない」が同居する）。

**規則 10 の引き直し**（是正で新たに誤りになる自分の記述を、**是正後の語**で引き直した）:

- **撤去する 4 区分を指すエイリアス・設定は 0 件である。** `src/vitest.config.ts` /
  `src/lingui.config.ts` / `src/platform/frontend/{vite.config.ts,tsconfig.app.json}` /
  `src/knowledge/frontend/tsconfig.json` / `templates/unit-template/frontend/tsconfig.json` /
  `src/eslint.config.js` の 7 ファイルを走査した結果、**向き先はすべて `platform/frontend/src/` の
  実体を持つ区分**（`config` `lib/i18n` `app/routing` `lib/api` `lib/auth` `utils` `components/*`
  `testing` `locales`）であり、撤去対象と 1 件も重ならない。
  **陽性対照**: `platform/frontend/src/locales` は `lingui.config.ts:43` と `eslint.config.js:320` が
  名指ししており、**同じ引き方で 0 件でないものが出る**。撤去対象の
  `knowledge/frontend/src/locales`（空枠）は 1 件も引っかからない。
- `IADR-0321` §影響 と `IADR-0325` §決定 1 は「ユニット直下は残す」と書いているが、
  **確定済み記録なので書き換えない**（`.claude/rules/traceability.repo.md` §凍結の射程）。
  置き換えは後続 IADR（`IADR-0361`）で記録し、索引の `IADR-0325` 行へ後継 ID を併記する。

## 4. 機械検査（`scripts/check-scaffolding-frames.js`。新設）

### 4.1 述語は 1 つだけ

> **追跡下に「`.gitkeep` のみのディレクトリ」が存在しない。**

「`.gitkeep` のみ」＝ **そのディレクトリの配下（任意の深さ）に、`.gitkeep` 以外の追跡ファイルが 1 件も無い**。
子孫まで見るのは、`a/.gitkeep` と `a/b/.gitkeep` のような入れ子でも
「実体で存在しているのか、枠なのか」を同じ 1 述語で決めるためである。

**この検査の対象にしないもの**（`ADR-0069` 決定 5 が明示的に外した）:
i18n カタログの網羅・feature 区分の実体・型 (b)（置き場所違い）の検出。
**型 (b) の常設検査を置くかは本 PR で決めない**（`ADR-0069` フォローアップ 4。issue #1195 §射程外）。

### 4.2 除外リスト（`ALLOWED_EMPTY_FRAMES`）

**各行に理由を書く**（`ADR-0069` 決定 5）。理由が空・短すぎるものは検査器自身が fail にする
（黙って外す道を用意しない。`check-route-manifest.js` の `SCREENS_NOT_IN_THE_ROUTE_TABLE` と同じ作法）。

| ディレクトリ | 理由 |
| --- | --- |
| `docs/batch` | `docs/README.md` が宣言する文書種別の出力先。まだ 1 件も書かれていない |
| `docs/errors` | 同上 |
| `docs/infra` | 同上 |
| `docs/integration` | 同上 |

**現時点で 4 件。`ADR-0069` 決定 1 が「`/new-project` が置く `.gitkeep` も射程外」と述べているが、
本リポジトリの `.claude/commands/` は 7 本（`adr-check` / `impl-feature` / `new-spec` /
`plan-feedback` / `plan-to-tasks` / `trace-check` / `verify`）で `new-project` を含まず、
`git grep -i gitkeep -- .claude/` は 0 件である**（陽性対照: 同じ引き方の
`git grep -c -i gitkeep -- scripts/` は `check-nul-bytes.js:1` を返す）。
**したがって除外リストへ先回りで足さない。** 実際に置かれたときに人が判断して足す。

### 4.3 fail-closed の作法（既存検査器と揃える）

- 走査母集合は `git ls-files`（未追跡は定義上の対象外）。**クラス B** なので
  `lib/worktree-state.js` の `warnIfResultMayDifferFromCi(…, MODE.TRACKED)` を呼び、
  `scripts.repo.test.js` の `TRACKED_CHECKERS` へ載せる（#683 / `IADR-0183`）。
- **0 件走査で静かに緑にしない** —— 走査件数の下限 `MIN_SCANNED` を持つ（#664 の門）。
- **除外リストの各行の理由が 10 文字未満なら fail**（理由なき除外を作らせない）。
- **除外リストに載っているが実在しないディレクトリがあれば fail**（腐った除外を残さない）。
- `--self-test` を持ち、CI では自己試験 → 本走査の 2 ステップで叩く。

### 4.4 自己試験（陽性・陰性の対）

`scripts/scripts.repo.test.js` へ足す。**実データが「射程内 0 件」なので、検出力は変異でしか示せない。**

| # | 種別 | 内容 |
| ---: | --- | --- |
| 1 | 陰性 | `--self-test` が通る |
| 2 | 陰性 | 実データ（追跡下すべて）で違反 0 件 |
| 3 | **陽性** | **一時的に `src/platform/frontend/src/stores/.gitkeep` を作って追跡下へ入れると exit 1 になり、そのパスを名指しする**（作業ツリーを必ず元へ戻す） |
| 4 | **陽性** | 除外リストから `docs/batch` を外すと 1 件を検出して非ゼロ終了する（issue の受け入れ基準そのもの） |
| 5 | 陰性対照 | 除外を戻すと緑へ戻る |
| 6 | 門 | `isScanTooSmall(0) === true`（0 件走査を fail 側に置く） |
| 7 | 門 | 理由が 10 文字未満の除外行を渡すと fail |
| 8 | 形 | 除外リストが 4 件で、4 件とも `docs/` 配下である（黙って伸びたら気付く） |

### 4.5 CI への配線

`.github/workflows/ci.yml` の **`static-checks` ジョブへ 2 ステップ追加する**
（`Self-test scaffolding frame checker` / `Check no .gitkeep-only directories`。
いずれも `if: ${{ !cancelled() }}`）。
🔴 **起動条件（`on:`）とジョブ名（＝必須チェック名）は変えない。** 既存ジョブへステップを足すだけである。

## 5. 併せて直す —— 雛形バックエンド README の「操作＝HTTP 端点」前提（#1196 の残件）

PR #1205（#1196）は `templates/unit-template/backend/Services/SampleService/README.md` の
9・15 行目を **「宣言ファイル領域が `templates/unit-template/README.md` の 1 枚に限られており、
#1195 が `templates/unit-template/**` を後から触るため」除外し、穴が残ることを申告した。**
本 PR がその `templates/unit-template/**` を触るので、**同じ語義でここを閉じる**。

- **9 行目** `Features/<集約>/<操作>/   # Endpoint.cs / Command.cs（または Query.cs）/ Handler.cs`
  —— 要素の列挙が HTTP 由来だけで、`ADR-0077` 決定 3 が退けた「操作＝登録表に登録された HTTP 端点」
  という読みへの導線が残る。**`*Consumer.cs`・常駐ジョブを列挙へ足し、契機の形で決めない旨を書く。**
- **15 行目** `Features/<集約>/<操作>/ … 段まで写す` —— この行自体に HTTP 前提は無いが、
  9 行目の鏡写しである。**9 行目を直したら「写す相手」の語も同じ語彙で揃える。**

`ADR-0077` の語義は PR #1205 が `templates/unit-template/README.md` へ書いた文面と同じものを使う
（2 つの雛形 README が割れないようにする）。**新しい決定はしない。**

## 6. 変更するファイル（宣言ファイル領域）

```
src/knowledge/frontend/src/{app,assets,hooks,locales,stores,testing,types,utils}/.gitkeep   （削除 8）
src/platform/frontend/src/{assets,hooks,stores,types}/.gitkeep                              （削除 4）
templates/unit-template/frontend/src/{app,assets,components,config,hooks,lib,locales,stores,testing,types,utils}/.gitkeep （削除 11）
src/platform/frontend/README.md
templates/unit-template/README.md
templates/unit-template/backend/Services/SampleService/README.md
scripts/check-scaffolding-frames.js                    （新設）
scripts/README.md
scripts/scripts.repo.test.js
.github/workflows/ci.yml
.ai-context/adr/IADR-0361_no-gitkeep-only-directories.md（新設）
.ai-context/adr/README.md
.ai-context/specs/20260903_issue-1195_empty-scaffolding-frames-removal-and-check.md（本書）
```

**並列**: #1163 が `scripts/README.md` と `scripts/scripts.repo.test.js` を触る。
**PR 直前に `origin/develop` を merge して解く。** #1135（frontend の Dockerfile / edge）とは交差しない。
**frontend の `src/` 実装ファイルは触らない**（空枠の撤去のみ）。

## 7. 受け入れ基準（issue #1195 の Given-When-Then をそのまま写す）

- [ ] `git ls-files "*.gitkeep"` が **4 件**（`docs/batch` `docs/errors` `docs/infra` `docs/integration`）
      だけを返し、`src/` と `templates/` 配下は **0 件**である
- [ ] 新設の検査器が「除外リストに載っていない `.gitkeep` のみのディレクトリ」**0 件**を報告し、
      除外リストの各行に理由が書かれている
- [ ] 除外リストから `docs/batch` を一時的に外すと **1 件を検出して非ゼロ終了する**（陽性対照）
- [ ] `src/platform/frontend/README.md` が planning#510 の裁定待ちに言及せず、
      **`ADR-0069` 決定 3 の (a)/(b) の区別**を本文に持つ
- [ ] `templates/unit-template/README.md` の `src/` 直下ツリーに `.gitkeep` の記載が 1 つも無く、
      **ツリー全体の正本が計画 `13_frontend-stack` §ディレクトリ構成 であることを指している**
- [ ] `cd src && pnpm run typecheck` / `lint` / `test` / `build` / `format:check` が成功する
- [ ] `node scripts/check-doc-links.js` ／ `gen-knowledge-graph.js --check` ／
      `check-trace-blocks.js` が成功する
- [ ] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が成功する
- [ ] `scripts/README.md` の表に新設の検査器が 1 行として載っている

## 8. 検証結果（実行したコマンドと出力。2026-09-03）

| コマンド | 結果 |
| --- | --- |
| `git ls-files "*.gitkeep"` | **4 件**（`docs/{batch,errors,infra,integration}`）。`src/` と `templates/` は **0 件** |
| `node scripts/check-scaffolding-frames.js --self-test` | **20 件 OK** |
| `node scripts/check-scaffolding-frames.js` | 追跡下 3008 件を走査 / 射程外 4 件を除外 / **違反 0 件** |
| **陽性対照**: 追跡下へ `src/platform/frontend/src/stores/.gitkeep` を足す | **exit 1**・パスを名指し。撤去して戻すと **exit 0**（陰性対照） |
| **陽性対照**: 除外から `docs/batch` を外す | **1 件検出**（`docs/batch`）。戻すと 0 件 |
| `node scripts/check-doc-links.js` | OK（1130 件） |
| `node scripts/check-trace-blocks.js` | OK（168 件） |
| `node scripts/gen-knowledge-graph.js --check` | OK（in-repo 5250 件） |
| `node scripts/check-nul-bytes.js` | OK（3008 件） |
| `node scripts/check-cross-repo-refs.js` | OK（2912 件） |
| `node scripts/check-plan-id-qualification.js` | OK（2408 件） |
| `node scripts/check-doc-type-vocabulary.js` / `check-doc-status-vocabulary.js` | OK |
| `node scripts/check-reading-budget.js` | OK（3 集合とも 51,200 バイト内） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **688 tests passed**（IADR 番号を一時的に空き番へ寄せて実測。下記 ★） |
| `pnpm run lint` | **0 errors / 9 warnings**（warning はすべて既存の `react-refresh/only-export-components`） |
| `pnpm run format:check` | All matched files use Prettier code style |
| `pnpm run typecheck` / `test` / `build` | **未 populate の submodule（`src/ai-stock-trading`）に起因する失敗のみ**（下記） |

### 環境に起因する失敗（本変更が原因ではないことの実測）

`git submodule status` = `-75075404… src/ai-stock-trading`（**先頭 `-` ＝未 populate**）。
このため `platform/frontend/src/features/index.ts` の
`import … from '@ai-stock-trading/features'` が解決できず、次が落ちる。

- `typecheck`: `platform/frontend` のみ **TS2307 の 1 件**。
  **`knowledge/frontend` と `templates/unit-template/frontend` は Done**（＝本 PR が触った側は緑）
- `test`: 101 ファイル中 **96 passed / 5 failed**（941 件中 934 passed）。
  5 件のうち 4 件（`app/Layout.test.tsx` / `app/routing/{breadcrumbs,initialChunk,router}.test.ts`）は
  `Failed to resolve import "@ai-stock-trading/features"` を出典に持つ。
  残り 1 件（`lib/api/orvalMutator.test.ts`）は**ローカル Node 24 だけの赤**で CI（Node 22）は緑である
- `build`: 同じ TS2307 の 1 件のみ

**陽性対照**: 同じ実行で 934 件が緑であり、走査そのものは機能している。

### `dotnet build`（雛形）を走らせていない理由

**本 PR は C# ファイルを 1 つも触っていない。** `git diff --cached --name-only` の拡張子内訳は
**`.gitkeep` 23 / `.md` 7 / `.js` 2 / `.yml` 1** で、`.cs` / `.csproj` / `.slnx` は **0 件**である。
`template-backend-build` と `build-and-test` の入力は変わらない。

### ★ IADR 番号について（既知の赤）

**`node scripts/check-adr-numbering.js` は `IADR-0361`〜`0365` を欠番として 5 件検出する。**
本 PR の実装 ADR は**オーケストレータからの割り当て（`IADR-0361`。仮番号）**であり、
**マージ時に改番される**前提である。現在の最大は `IADR-0360`。
上の 688 件の実測は、検証のあいだだけ `IADR-0361` へ寄せて取ったものである
（**取得後 `IADR-0361` へ戻した**。`grep -rn "IADR-0361"` = 0 件で確認）。
