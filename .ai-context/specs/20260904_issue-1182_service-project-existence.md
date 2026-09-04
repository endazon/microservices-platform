---
title: 作業仕様書 — submodule pin 更新時に AST の SERVICE_PROJECT（csproj）が実在するかを検査する（#1182）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0007
  - IADR-0067
  - IADR-0068
  - IADR-0070
  - IADR-0372
author: claude
created: 2026-09-04
updated: 2026-09-04
plan_refs: []
---

# 作業仕様書: submodule pin 更新時に AST の SERVICE_PROJECT（csproj）が実在するかを検査する

## 起点となる計画書（トレーサビリティ）

- 起点 issue: #1182（`chore(NFR)`）
- 関連 ADR: `ADR-0007`（ローカル実行環境・デプロイ）
- 関連 IADR: `IADR-0068`（image-mapping ドリフト検査の方式）／`IADR-0070` 決定 2（AST は
  単一パラメータ化 Dockerfile ＋ build args ＋ ユニットルート context）／`IADR-0067`（イメージ
  ビルド可否は `images.yml` が担う）
- 本作業の実装 ADR: `IADR-0372`
- NFR: 運用・保守（採番された NFR 番号は無い。`.claude/rules/traceability.repo.md`
  「メタ作業（規約・検査器・文書統制）は代表例で、製品の作業にも当たる番号が無いことはある」に該当）

## 目的・背景

`src/ai-stock-trading`（AST）の submodule pin を進めたとき、**MSP 側が持つ「AST ツリー内のパス」
（`SERVICE_PROJECT` ＝ csproj パス、および `dockerfile`）が pin されたツリーに実在しなくなり、
イメージビルドが `MSBUILD : error MSB1009` で落ちる**事故が起きている。

### 同型の事故が 2 回起きていること（履歴で実測）

`.claude/rules/traceability.repo.md`「検査器・規約の追加は『同型の事故が 2 回起きたら』を条件とする」
に照らして、**issue の記述を転記せず**自分で履歴を引いた。

```
$ git rev-parse --is-shallow-repository
false
$ git log --format='%h %ad %s' --date=short -G'SERVICE_PROJECT' -- deploy/docker-compose.yml scripts/k8s-local-images.sh
ac3df666 2026-09-03 refactor(ADR-0031,ADR-0032): AST ユニットを型付きルート契約で合成し、旧契約の互換ブリッジを撤去する (#1178)
36a8bc8a 2026-08-07 fix(NFR,FR-14): AST の *.Worker → *.Api 改名に deploy 面を追随させる (#577)
10d79e03 2026-07-18 feat(IADR-0072): AST 監視銘柄（SC-02 watchlist）の /bff/monitor/* プロキシ登録と MarketMonitorService 登録 (#294)
a6333ccf 2026-07-18 feat(IADR-0071): AST SC-02/03 の /bff/risk-controls/* プロキシ登録と submodule 再pin (#289)
2cda8083 2026-07-18 feat(IADR-0070): AST フロント/設定画面を MSP SPA へ組み込む (#285)
```

- 1 回目 = `36a8bc8a`（#577）。#564 の pin bump（`655e2ed` → `91d52c2`）で `*.Worker` → `*.Api`
  改名に追随できていなかった。**是正は別 PR**（pin bump のマージ後に develop が赤くなった）。
- 2 回目 = `ac3df666`（#1178）。`0844b58` → `7507540` の pin bump で AST が単一プロジェクト＋VSA へ
  移行し `src/` 段と `*.Api` が消えた。是正は同一 PR 内。
- 上 3 件（`10d79e03` / `a6333ccf` / `2cda8083`）は事故ではなく**新規登録**である（`-G` は
  「その語を含む行が動いた」コミットを拾うので、事故だけを数えると 3 件多くなる）。

`git log -- src/ai-stock-trading` は **2026-07 以降で 20 件超の pin bump**（大半は dependabot の
`build(deps): bump src/ai-stock-trading …`）を返す。**次の bump でも同じことが起きる。**

### 何が穴か

既存の `scripts/check-image-mapping.js` は **compose の `build` 定義 ⇔ `k8s-local-images.sh` の
`MAPPING` の同値**だけを見る（`IADR-0068` / `IADR-0070`）。この 2 つは**同じ値**を持つので、AST が
樹形を変えると**両方が同時に古くなり、ドリフトは 0 のまま**通る。実在は誰も見ていない。

## 対象範囲

### 母集合（自分で走査した。issue の記述は転記していない）

**走査 1（陽性対照つき）** —— MSP が持つ「AST ツリー内のパス」を全数で引く。

```
$ git grep -l "ai-stock-trading" -- . ':!src/ai-stock-trading' ':!.ai-context' ':!CHANGELOG.md' | wc -l
   → 98 ファイル（陽性対照: 0 件ではない。以下はここからの絞り込み）
```

98 件のうち、**AST ツリー内の相対パスをビルド／デプロイの入力として持つ**のは次の 5 群だけである
（残りは submodule の path 名・realm・alias・文書・ESLint 等の言及であり、AST ツリー内のパスを
持たない）。

| # | 箇所 | 形 | 本作業の対象 |
| --- | --- | --- | --- |
| A | `deploy/docker-compose.yml`（3 サービス） | `build.args.SERVICE_PROJECT` ＋ `build.dockerfile`（context = `../src/ai-stock-trading`） | **対象** |
| B | `scripts/k8s-local-images.sh` の `MAPPING`（3 エントリ） | `SERVICE_PROJECT=…` ＋ 4 フィールド目の dockerfile（context = `src/ai-stock-trading`） | **対象** |
| C | `src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj:20` | `ProjectReference` → `..\..\..\..\ai-stock-trading\backend\Bff\AiStockTrading.Bff.Endpoints\AiStockTrading.Bff.Endpoints.csproj` | 対象外（下記） |
| D | `src/platform/backend/Bff/Platform.Bff/Dockerfile:11` | `COPY src/ai-stock-trading/backend/Bff/ …` | 対象外（下記） |
| E | `src/platform/frontend/Dockerfile:26,37` | `COPY src/ai-stock-trading/frontend/package.json …` / `COPY src/ai-stock-trading/frontend/ …` | 対象外（下記） |

**C・D・E を対象外にする理由**（「射程を広げない」ではなく、**既に別の門が実測で落とす**）:

- **C は `ci.yml` の backend ビルドが落とす。** `src/platform/backend/backend.slnx` は
  `Bff/Platform.Bff/Platform.Bff.csproj` を含み、その `ProjectReference` は無条件である。
  `ci.yml` の当該ジョブは `git submodule update --init` で AST を populate してから
  `dotnet build` するので、参照先の csproj が消えれば**その場で赤**になる。
- **D・E はイメージビルドが落とす**（`integration-stack.yml:71` のコメントに「これが無いと bff と
  frontend のイメージビルドが落ちる（実測 run 32554145102: `COPY src/ai-stock-trading/backend/Bff/`
  → not found）」と実測が残っている）。**A・B と違うのは、C・D・E は「参照が消えたら必ず落ちる場所が
  すでに必須ジョブにある」点**である。A・B だけが「安価な検査を素通りして、最も遅いイメージビルド段で
  初めて落ちる」。
- したがって本作業は **A・B（＝ SERVICE_PROJECT と、同じタプルの dockerfile）に絞る**。C・D・E へ
  検査を広げても、より早く落ちる場所は増えない。

**走査 2 — CI が submodule を populate するか**（issue は「static-checks 等」と書いているが、
`static-checks` は populate しない。**転記せず自分で引いた**）。

```
$ git grep -n "^\s*submodules:" -- .github/workflows/    → 0 件
$ git grep -c "actions/checkout" -- .github/workflows/   → 17 ファイル・27 ステップ（陽性対照）
$ git grep -n "submodule update" -- .github/workflows/   → 11 件
```

🔴 **`actions/checkout` の `submodules:` は 1 箇所も使われていない。** 本リポジトリの populate は
**`git config --file .gitmodules … | xargs -r -n1 git submodule update --init` という手書きの
4 行イディオム**で行われており、`ci.yml`（3 箇所）・`codeql.yml`・`frontend-tests.yml`・
`frontend.yml`（2 箇所）・`images.yml`・`integration-stack.yml`・`integration.yml`・`security.yml`
の 11 箇所にある。**`image-mapping.yml` には無い**（＝検査を足す先で populate が要る）。

「0 件だった」を「無い」と読むと結論を間違える箇所なので、`actions/checkout` の件数を陽性対照として
対で置いた。

### 対象外（明示）

- `SERVICE_DLL` の実在検査。**ビルド成果物の名前**であって pin されたツリーには存在しないため、
  静的には検査できない（`images.yml` の実ビルドが担う）。
- AST リポジトリ側の作業（`IADR-0120`。本リポジトリからは変更しない）。
- ブランチ保護の必須チェック構成の変更（リポジトリ設定であり実装セッションの裁量外。
  `.ai-context/specs/20260807_issue-570_ast-project-rename.md` の申し送り (b) と同じ理由）。

## 設計

`scripts/check-image-mapping.js` を**拡張する**（新設しない）。理由は `IADR-0372` に記す。

### 追加する純粋ロジック（`scripts.repo.test.js` から単体試験する）

1. `collectSubmodulePaths({ mappingEntries, composeTargets, submodules })`
   → compose の build 定義と MAPPING エントリから、**context が submodule 配下**であるものを選び、
   `{ source, id, submodule, relPath, kind }` の配列を返す。`kind` は `dockerfile` / `SERVICE_PROJECT`。
   compose の context は `normalizeComposeContext`（既存）でリポルート相対へ正規化してから判定する。
2. `computeMissingPaths(entries, exists)`
   → `exists(repoRelPath) => boolean` を**注入**して、実在しないものを violation にする。
   注入するので、実 submodule を populate せずに陽性・陰性の両方を自己試験できる。

### 実行時の分岐（`main()`）

- `submodulePaths()`（`.gitmodules` 由来。`check-doc-links.js` と同じ導出）で submodule 一覧を得る。
- **導出が 0 件なら赤**（`empty-submodule-paths`）。**0 件走査を緑にしない**（`checkTree()` の
  既存 `empty` 判定と同じ思想）。
- submodule が未 populate（ディレクトリが空）なら:
  - `--require-submodule` 付き → **赤**（`submodule-unpopulated`）。
  - 付いていない → **notice を出して skip**（fail-open）。「検査していない範囲」を件数つきで明示する。
- populate 済みなら `fs.existsSync` で実在検査する。

`--require-submodule` は**締める方向の明示フラグ**であり、抜け道の環境変数ではない
（先例: `check-static-egress.js --require <dist>`）。CI は必ずこれを付けて呼ぶので、**populate
ステップが将来壊れたら緑ではなく赤になる**（「陰性結論には陽性対照を」の実装形）。

### CI 配線

`.github/workflows/image-mapping.yml` に、既存 11 箇所と同一の populate イディオムを 1 ステップ足し、
実チェックの呼び出しへ `--require-submodule` を付ける。

- **`on:`（起動条件）は変えない**（`push: [develop, main]` / `pull_request: [opened, synchronize, reopened]`）。
- **ジョブ名 `image-mapping` は変えない**（必須チェック名は `docs/ai-workflow.md` の表が正。ジョブ名を
  変えると恒久 pending になる）。
- 新しいワークフローは作らない（必須チェック名を増やさない）。

## 受け入れ基準

- [x] `deploy/docker-compose.yml` の各 AST サービスの `SERVICE_PROJECT` が指すパスが、
      `src/ai-stock-trading` の実ツリーに実在しなければ赤になる
- [x] `scripts/k8s-local-images.sh` の `MAPPING` についても同じ検査が効く（片方だけ見ない）
- [x] 変異試験: 実在しないパスへ書き換えたら赤くなることを `scripts/scripts.repo.test.js` で固定する
      （陽性・陰性の対）
- [x] submodule 未 populate のときは notice を出して skip し、「検査していない範囲」を明示する。
      **ただし `--require-submodule` 付きなら赤**
- [x] 走査対象が 0 件なら赤（0 件走査で緑にしない）
- [x] `image-mapping.yml` の `on:` とジョブ名が差分で変わっていない
- [x] `node scripts/check-image-mapping.js --self-test` と
      `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑
- [x] 現行 pin（`7507540`）に対して実チェックが緑（＝陽性対照。検査が「常に緑」ではないことは
      変異試験が示す）

## テスト方針

- `scripts/check-image-mapping.js --self-test`: 既存ケースに `collectSubmodulePaths` /
  `computeMissingPaths` の陽性・陰性を追加する。
- `scripts/scripts.repo.test.js`: 同じ 2 関数を単体試験する（存在判定を注入するので submodule 不要）。
  さらに **`--require-submodule` 付きの子プロセス実行が、未 populate で exit 1 になる**ことを固定する。
- 実測: 現行 pin のツリーを `gh api repos/endazon/ai-stock-trading/git/trees/<sha>?recursive=1` で引き、
  3 本の csproj と `backend/Dockerfile` が実在することを確認する（ローカルは未 populate のため、
  実チェックの緑は CI で確認する）。

## 計画書との差異

- 差異: なし。`IADR-0068`（対応表の整合のみを見る）の射程を**実在まで広げる**が、`IADR-0067`
  （ビルド可否は `images.yml`）とは競合しない —— 実在検査はビルドではない。

## 未決事項

1. **`image-mapping.yml` が AST リポジトリの可用性に依存するようになる**（populate の追加）。
   既に 11 ワークフローが同じ依存を持つため新規のリスクではないが、`image-mapping.yml` は軽量・
   常時実行の設計だったので、実行時間が数十秒増える。
2. 母集合 C・D・E（`Platform.Bff.csproj` の `ProjectReference` ／ 2 つの Dockerfile の `COPY`）は
   既存の必須ジョブが落とすため対象外にした。**もしこの前提が崩れたら**（例: AST の BFF を
   `ci.yml` の対象から外す等）、本検査の射程を広げる必要がある。
