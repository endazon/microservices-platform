---
title: 作業仕様書 — Renovate と Husky を導入する（SPA 移行第 5 段の切り出し 1/2・#768）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0031
  - IADR-0115
  - IADR-0121
  - IADR-0203
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs: []
---

# 仕様書: Renovate と Husky の導入（#768）

## 起点となる計画書（トレーサビリティ）

- FR / UC / SC: なし（**NFR / 運用保守**。計画の非機能要件表に当たる番号が無く、
  `.claude/rules/traceability.repo.md`「無採番 `NFR`」の場合 2 に当たり**環流しない**）
- 関連 ADR: 計画 `ADR-0031`。実装 ADR は [[IADR-0121]] 決定 1（第 5 段 = 運用系）/ [[IADR-0115]]（キット同期の
  分類）/ 本作業で新設する [[IADR-0203]]。関連 issue: #768（本件）/ #493（親。Knip / Plop を引き受け open で
  残る）/ #446（その親）/ #562（フロントの format ゲート。手元フックの前提）/ #260（Dependabot gitsubmodule）
- 計画書リンク: **参照できない**。`planning/` submodule が本 worktree で未 populate（`ls planning` = 0 件）で、
  計画 `06_technical/13_frontend-stack` の採用技術一覧を直接読めていない。IADR-0121 決定 1 の引用
  （「第 5 段 = 運用系（Knip / Plop / Renovate / Husky）」）と issue #493 / #768 本文で代用した

## 対象範囲

- 対象: 直下 `renovate.json`（新規）/ `.husky/`（新規）/ `src/package.json`（`husky` と `prepare`）/
  `src/pnpm-lock.yaml` / `docs/adr/IADR-0203` ＋索引
- **`.github/dependabot.yml` は当初「棲み分けの注記を入れる」つもりだったが取り止めた** —— キット配布物の
  **分類 A（バイト一致）**で、注記を入れると `check-kit-sync.js` が落ちることを実測した（下記「実測」）
- 対象外: **Knip**（#452 待ち）・**Plop**（第 4 段待ち。親 #493 が引き受ける）。`.github/workflows/`
  （起動条件・必須チェックを変えない）。`src/*/backend/**`（本環境に dotnet が無い）。フロントのアプリコード。
  **lint-staged / Commitlint**（下記「計画書との差異」）

## 母集合の引き直し（着手時に自分で引いた。issue 本文の一覧は転記していない）

除外パスは `node_modules`（そもそも不在）・`.git`・`planning/`（未 populate の別リポ submodule）・
`src/ai-stock-trading`（同左）のみ。**拡張子で絞っていない**（規則 3）。

- **軸 1（パスから引く）** `find` で `renovate* / .renovaterc* / *husky* / *lintstaged* / .lintstagedrc* /
  *commitlint* / plop*` → **0 件**。
- **軸 2（誤りの側の語で全文走査）** `grep -rIl`（該当ファイル数）: `husky` 1・`lint-staged` 2 は過去仕様書と
  `feedback/` の**言及のみ**。`lintstaged` / `commitlint` / `plop` / `"prepare"` / `core.hooksPath` /
  `simple-git-hooks` / `pre-commit` は**各 0**。`renovate` 8 ＋ `Renovate` 11 = 19 件はすべて言及（bot 著者の
  除外リスト・過去仕様書・IADR-0060 / IADR-0121・`templates/unit-template/README.md`）で**設定は 1 件も無い**。
- **軸 3（宣言の側から引く）** `package.json` をパスで全列挙し 5 本すべて実読（`src/` / `platform/frontend` /
  `knowledge/frontend` / `packages/ui` / `templates/unit-template/frontend`）。**どれにも当該 devDependency は
  無く、`prepare` スクリプトも無い。**

**実測した現状**: Renovate = 未導入 / Husky = 未導入 / `.github/dependabot.yml` の対象 = `github-actions` と
`gitsubmodule` のみ（`npm` / `nuget` / `pip` はコメントアウトの例のまま）。issue 本文と一致した。
**新たに誤りになる自分の記述の引き直し**（規則 10）: `docs/tech/tech-requirements.md` と
`docs/operations/operations.md` を `dependabot` / `依存.*更新` / `prettier` で走査 → **0 件**（両文書はもともと
依存更新ツーリングを扱っておらず、追随すべき記述は発生しない）。

## 設計

### 1. Renovate と Dependabot の担当範囲

| エコシステム | 担当 | 理由 |
| --- | --- | --- |
| `npm`（`src/` の pnpm workspace） | **Renovate** | どちらも見ていない**無主地**。pnpm workspace ＋ `overrides` を持つ本リポでは lockfile 更新とグルーピングの制御が効く |
| `github-actions` | Dependabot（現状維持） | 既に稼働。`check-action-versions.js` と組で回っている |
| `gitsubmodule` | Dependabot（現状維持） | #260 の **3 リポ横断オーナー承認済み決定**（「Dependabot、Renovate ではない」）。private な `planning` を registries ＋ `PLANNING_REPO_TOKEN` で引く構成が動いている |

**重複は `enabledManagers: ["npm"]` で機械的に排除する**（Renovate は既定で `github-actions` と
`git-submodules` も見るため、書かないと二重に PR が出る）。Dependabot 側の `npm` はコメントのまま**有効化せず**、
**`.github/dependabot.yml` へは注記も入れない**（分類 A。実測で `[drift]`）。棲み分けの記録は
`renovate.json` の `description` と [[IADR-0203]] が持つ。

### 2. Husky の設置場所

- git ルートに `package.json` は無く pnpm workspace ルートは `src/` である。husky v9 の CLI は
  **cwd に `.git` が在ることを要求**するため、`src/package.json` の `prepare` を **`cd .. && husky`
  （git ルートで実行）**とする（`husky` は pnpm が PATH へ通すので `cd` 後も解決できる）。末尾の `|| true` は
  **CI の `pnpm install --frozen-lockfile` を husky の失敗で落とさない**安全弁である。
- `core.hooksPath` は **`.husky/_`**（相対）。git は相対パスを**作業ツリーのトップ基準**で解決するため、
  worktree ごとに `.husky/` の有無で自然に切り替わる。フック本体 `.husky/pre-commit` / `.husky/commit-msg` は
  コミットし、`.husky/_/` は husky が生成して自身が置く `.gitignore`（`*`）で追跡対象外になる
  （root `.gitignore` の変更は不要）。

### 3. フックが強制するゲートと CI の対応

**手元フックは CI ゲートの前倒しであり、置き換えでも上乗せでもない**（#562）。**CI に無いゲートは作らない。**

| フック | 実行するもの | CI の対応 | 関係 |
| --- | --- | --- | --- |
| `pre-commit` | ステージ済み `src/**` へ `prettier --check` | `frontend.yml` の `build-test` / `Format check (prettier)` | ステージ済みのみの部分集合 |
| `commit-msg` | `node scripts/check-commit-messages.js --title <件名>` | `ci.yml` の `commit-messages` ジョブ | **件名のみ**の部分集合（本文のクロスリポ参照検査は CI が持つ） |

`typecheck` / `lint` / `test:coverage` は**入れない** —— 横断で秒〜分かかり、1 コミットごとに走らせると
非対話の AI の手が止まる。いずれも CI が全量で強制している。**整形の対象範囲は `src/.prettierignore`
ただ 1 つが持つ**（#562）ため、フックは**グロブを複写しない** —— `src/` 配下のステージ済みファイルを
cwd = `src/` で prettier へ渡すだけで、除外は prettier が解決する。

**安全弁**: `src/node_modules` が無い / `pnpm` 不在なら pre-commit は理由を表示して **exit 0**（fail-open）。
`.claude/hooks/`（`guard-bash` / `guard-secrets` / `check-impl`）とは**役割が重ならない**ことを実読で確認した
（Bash 実行前のブロックと編集後の警告であり、git のフックポイントには結線していない）。

`renovate.json` は `extends: ["config:recommended"]` ＋ 上記の担当分割 ＋ `develop` 基準・週次・
`semanticCommitScope: "NFR"`（bot 除外が効かなくてもコミット規約に適合する）だけに留め、
グルーピング等の最適化は入れない（既定でも同一依存の全出現は 1 PR にまとまる）。

**`ignorePaths` に `planning/**` を足した**（AI レビューの指摘を実測で裏取りした）。
着手時の母集合走査では `planning/` を「未 populate の別リポ submodule」として除外していたが、
**pin どおり populate して数え直すと `package.json` が 2 件実在する**。

```console
$ find planning -name package.json -not -path "*/node_modules/*"
planning/tools/docs-site-kit/site-template/package.json
planning/tools/impl-handoff-kit/generators/package.json
```

`enabledManagers: ["npm"]` は**エコシステムを絞るだけでパスは絞らない**ため、除外はパス側で明示する必要がある。
Renovate の `cloneSubmodules` は既定 `false` なので**実害は低い**と見られるが、
**本セッションでは Renovate を実走できず、この経路は検証できない**。検証できないものは安全側へ倒す。

なお `src/ai-stock-trading/**` は着手時から除外していた（同じ理由）。**除外の 3 件は「本リポが所有しない
package.json を持つ場所」で揃っている**。

### 4. 外部送信（egress）

`08_data-egress-policy` が禁じるのは**成果物（SPA / Storybook）からの外部 CDN・Web フォント・analytics** で、
`check-static-egress.js` が成果物を走査して検査している。本作業は成果物に 1 バイトも入らない（`renovate.json` は
bot が読む設定、`.husky/` は開発機の git フック、`husky` は devDependency）。Renovate 側でテレメトリ類は一切
有効化しておらず、`$schema` は**エディタ補完のための URL 文字列**で実行時の取得は起きない。

## 受け入れ基準（issue #768 より転記）

- [x] Renovate の設定が入り、npm の更新 PR が出る対象範囲が明示されている（`enabledManagers` / `ignorePaths`）
- [x] Dependabot との重複が無い（npm = Renovate / `github-actions`・`gitsubmodule` = Dependabot）
- [x] Husky のフックが入り、**実際に発火することを実測**した（下記）
- [x] フックが強制するのは既に CI にあるゲートだけである（上表）
- [x] `pnpm run typecheck` / `lint` / `format:check` / `test:coverage` が緑（下記）

## テスト方針

フックは**変異試験**で見る（落ちること・直せば通ることの**両方**。片方だけでは「常に落ちる」「常に素通り」を
見分けられない）。Renovate は実走できないため**構文妥当性まで**を実測し、PR 生成は**確認できていないと明記する**。
**新しい検査器は足さない**（規約は同型の事故が 2 回起きてから。本件は 1 回目）。

## 実測

### 依存の追加（**ここで lockfile を壊しかけた**）

```
$ cd src && pnpm install
+ husky 9.1.7 …… Done in 5.7s using pnpm v10.33.0
$ git config --get core.hooksPath → .husky/_          ← prepare が走った
```

**ただし 1 回目の install は lockfile から importer `ai-stock-trading/frontend` を丸ごと落とした**
（`10 insertions(+), 104 deletions(-)`）。本 worktree で可変ユニットの submodule が**未 populate** だったためで、
これを commit すると CI の `--frozen-lockfile` と AST の合成が壊れる。**CI と同じ手順**で populate し直した。

```
$ git submodule update --init src/ai-stock-trading   → checked out '7f69fb50…'
$ git checkout -- src/pnpm-lock.yaml && cd src && pnpm install
$ git diff --stat src/pnpm-lock.yaml → 1 file changed, 10 insertions(+)   ← husky のみ
```

### `.github/dependabot.yml` は編集できない（分類 A）

```
$ node scripts/check-kit-sync.js                      # 棲み分けの注記あり → 違反 2 件
    [drift] .github/dependabot.yml が分類 A なのにキットとバイト一致でない …
    [drift] scripts/scripts.test.js  が分類 A なのにキットとバイト一致でない …
$ git checkout -- .github/dependabot.yml && node scripts/check-kit-sync.js   # → 違反 1 件
```

**残る 1 件は本作業と無関係の既存事象**（`scripts/scripts.test.js` は触っていない）。本環境では比較先が隣接
クローン `../project-planning`（HEAD `5e53b9d`）に解決される一方、本リポの planning pin は `4d6a7d6` で、pin より
後の planning#368 がキットへテストを 1 件足しているためである（`diff` で差分が `execSync` → `execFileSync`
の 1 件だけと確認）。**CI では pin 済み submodule と比較されるため出ない。**

### フックの発火（落ちる系 2 型・通る系・除外の対照）

```
# ① pre-commit（整形）— 落ちる
$ printf 'export const probe   =    1\n' > src/platform/frontend/src/__hookprobe.ts
$ git add … && git commit -m "chore(NFR): hook probe"
[warn] platform/frontend/src/__hookprobe.ts / [warn] Code style issues found in the above file.
✗ pre-commit: 整形されていないファイルがある（CI の frontend.yml「Format check (prettier)」と同じゲート）
husky - pre-commit script failed (code 1)   EXIT=1   → HEAD は動かず（コミットは作られない）

# ② commit-msg（規約）— 落ちる（整形を直して pre-commit を通した上で）
$ cd src && pnpm exec prettier --write platform/frontend/src/__hookprobe.ts
$ git commit -m "bad message"
Checking formatting... All matched files use Prettier code style!     ← pre-commit は通過
✗ PR タイトルが規約違反:  bad message  - 形式が `種別(起点ID): 要約` に一致しない
✗ commit-msg: コミット件名が規約 `種別(起点ID): 要約` に違反している
husky - commit-msg script failed (code 1)   EXIT=1

# ③ 通る系（両フックが発火して緑）
$ git commit -m "chore(NFR): hook probe"
All matched files use Prettier code style! / ✓ PR タイトルが規約に適合
[chore/nfr-renovate-husky 52d4a9e] chore(NFR): hook probe   EXIT=0

# ④ 除外の単一情報源が効いていること（.prettierignore が外す *.md を未整形で作る）
$ printf '*   a\n*  b\n\n\n#  head\n' > src/__hookprobe.md && git add … && git commit -m "chore(NFR): hook probe ignored path"
All matched files use Prettier code style!   ← 検査対象に入っていない   EXIT=0
# 対照（空振りでないことの確認）: 除外を外すと同じファイルは落ちる
$ cd src && pnpm exec prettier --check --ignore-path <空ファイル> __hookprobe.md
[warn] __hookprobe.md   EXIT=1

# ⑤ 検証用の変更を混入させていないこと
$ git reset --soft HEAD~2 && git restore --staged <probe 2 件> && rm <probe 2 件>
$ git log --oneline -1 → fde7252（base に戻った）
$ git status --porcelain -uall → M src/package.json / M src/pnpm-lock.yaml
                                 / ?? .husky/{commit-msg,pre-commit} / ?? docs/specs/…md / ?? renovate.json
```

**「常に落ちる」でも「常に素通り」でもないことを、同じフックで両方向から確かめた。** probe 2 件と検証用
コミット 2 件は残っていない（`--soft` のみ。破壊的な `reset --hard` は使っていない）。

### Renovate 設定の検証（**実走できていない**）

```
$ npx --yes --package renovate -- renovate-config-validator
 INFO: Validating renovate.json / INFO: Config validated successfully   EXIT=0
```

**確認できたのはここまでである。** Renovate は GitHub App として導入されて初めて動くため、**実際に npm の
更新 PR が出ることは本セッションでは確認していない**（有効化までは何も起こさず、Dependabot の現行 2 エコシステムに
影響もしない）。代替の確認手段は、有効化後の Dependency Dashboard issue と初回オンボーディング PR を見ること
である。**「動くはず」とは書かない。**

### 検証コマンドの結果

| コマンド | 結果 |
| --- | --- |
| `pnpm run typecheck` / `format:check` | **緑**（4 プロジェクト Done。`ai-stock-trading/frontend` を含む）/ **緑** |
| `pnpm run lint` | **緑**（0 errors, 9 warnings。既存の `react-refresh/only-export-components` 等） |
| `pnpm run test:coverage` | **緑**（Test Files 71 / Tests 922 passed。Stmts 96.96% / Branch 91.01% / Func 93.22%） |
| `scripts.test.js`（`REQUIRE_REPO_TESTS=1` 付きも） | **赤 1 件**（上記の既存 drift。落ちるのは `check-kit-sync` の同一アサーションのみ）。`scripts.repo.test.js` 単体は**緑** |
| `check-doc-links` / `check-adr-numbering` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-doc-type-vocabulary` / `check-doc-status-vocabulary` | **すべて緑**（632 / 欠番なし・索引と双方向一致 / 1617 / 1325 / 606 / 592 件） |

`check-static-egress.js` は `--require <dist>` で**ビルド成果物**を走査する検査器であり、本作業は成果物を
1 バイトも変えない（`renovate.json` / `.husky/` / devDependency はバンドルへ入らない）ため実行していない。

## 計画書との差異

- 差異: **あり**。計画 `13_frontend-stack` の第 5 段は Husky を **lint-staged / Commitlint と組**で挙げるが、
  本作業は **husky のフックのみ**を入れる。いずれも本リポの既存決定と衝突するためである。
  1. **lint-staged はグロブで対象を決める。** 整形範囲の単一情報源は `src/.prettierignore` ただ 1 つと #562 で
     確定しており、グロブを書くと**単一情報源が 2 本に割れる**。さらに lint-staged は git ルートで動くのに
     `.prettierignore` の解決は cwd = `src/` を要すため、誤ると**除外が静かに外れて**生成物（orval / Lingui）まで
     整形し、CI の `Codegen is up to date` を落とす。
  2. **Commitlint は規約の第 2 の情報源になる。** 正本は `.claude/rules/traceability.md` と
     `check-commit-messages.js` であり、別の規則表を持つと乖離する。フックからは**同じスクリプトを呼ぶ**のが
     正しい前倒しである。

  この判断は [[IADR-0203]] に記録した。**親 #493 は Knip / Plop と併せてこの 2 件も引き受けたまま open で残る**
  ため、計画一覧との完全一致の判定は #493 側で行う（本作業だけで一致を主張しない）。上記は**実装に閉じた
  事情**（単一情報源の置き方）で計画の技術選定を覆さないため、現時点で `/plan-feedback` は要しない。

## 未決事項

- **Renovate App の有効化**（利用者作業）。有効化されるまで設定は何も起こさない。
- 有効化後、初回のオンボーディング PR が `develop` を base に出ることの確認（`baseBranches` 指定済み・未実走）。
- **キット側（`impl-handoff-kit` の `.github/dependabot.yml`）へ「npm を Renovate 側で見る構成もある」旨の
  注記を入れるか。** 本リポからは分類 A のため書けない。配布先すべてに関わるため、ここでは判断しない。
