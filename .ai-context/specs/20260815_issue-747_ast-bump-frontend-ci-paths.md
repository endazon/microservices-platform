---
title: 作業仕様書 — AST submodule の bump でフロント CI を起動させる（paths への submodule パス追加。#747）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0134
  - IADR-0116
  - IADR-0182
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
related_specs:
  - "../adr/IADR-0134_spa-route-code-splitting-boundaries.md"
  - "../adr/IADR-0116_reimplementation-branching-and-pr-policy.md"
  - "../adr/IADR-0182_required-check-contexts-and-blocked-record.md"
  - "../../docs/ai-workflow.md"
---

# 作業仕様書: AST submodule の bump でフロント CI を起動させる（#747）

## 1. 起点と背景

起点は issue #747 である。`src/ai-stock-trading`（AST）submodule のポインタ更新は
`src/platform/frontend/src/features/index.ts` の**静的 import** を通じて初期ロードのバンドルを
増やすが、`frontend.yml` / `frontend-tests.yml` の `paths:` が `src/*/frontend/**` などしか
持たないため、**gitlink 1 エントリの変更（`src/ai-stock-trading`）はどのパターンにも一致せず、
build も `check-chunk-budget` も走らない**。実測では 3 回の bump が素通りし、+35.51 kB が
無関係な PR #746 で初めて表面化した（切り分けの実測は `scripts/chunk-budget-baseline.json` の
`$comment_initialTotalBytes` に残っている）。

### 起点 ID を無採番 `NFR` にした理由

計画の非機能要件は `NFR-01`〜`NFR-27` で、**27 件すべてが稼働する製品の要件**である
（性能 / 可用性 / スケーラビリティ / セキュリティ / 運用・保守 / 拡張性）。本作業は
**CI の起動条件＝検査器の穴を塞ぐ工程側のメタ作業**であり、当たる番号が無い。
`.claude/rules/traceability.md` の例外 2（ID 列はあるが該当番号が無いメタ作業）に当たるため
無採番 `NFR` を用い、**計画側へは環流しない**（planning#311 の裁定どおり、工程の規律を
製品の品質要件の表へ混ぜない）。

## 2. 対象範囲

- **対象**: 案 A のみ。`.github/workflows/frontend.yml` と `.github/workflows/frontend-tests.yml` の
  `push` / `pull_request` 両方の `paths:` に `src/ai-stock-trading` を加える。
- **対象**: 同型の取りこぼしの回帰固定（`scripts/scripts.repo.test.js`。理由は §5）。
- **対象外**: 案 B（AST features を遅延 import へ変え初期ロードから外す）。**`IADR-0134` の
  分割境界の改定にあたり IADR が要る**ため、本 PR では実装せず **PR 本文で別 issue への
  切り出しを提案するに留める**（1 issue = 1 PR。IADR-0116 規約 1）。
- **対象外**: 新規 IADR の起票（本波の IADR 枠は別 issue が使う）。
- **対象外**: `scripts/chunk-budget-baseline.json` の床の引き直し（#578 の PR で実測へ更新済み）。

## 3. 母集合の引き直し（自分で引いた。issue 本文の一覧は転記していない）

**引く対象**: 「`paths:` を持つワークフローのうち、submodule の変更を取りこぼすもの」。
**誤りの側**（＝トリガが絞られている側 = `paths:` / `paths-ignore:` を持つ）から引き、
軸を 3 本立てた。走査は `.github/workflows/` 配下の**全ファイル**（拡張子で絞らず、
`git ls-files` でワークフロー相当の追跡ファイルが 16 件・`.example` / `.yaml` の別名が
無いことも確認した）。

### 走査 1 —— `paths:` / `paths-ignore:` を持つワークフロー（誤りの側）

```console
$ cd .github/workflows && grep -ln '^\s*paths\(-ignore\)\?:' *.yml
codeql.yml
copilot-setup-steps.yml
frontend-tests.yml
frontend.yml
openapi.yml
```

全 16 ワークフロー中 **5 件**（`paths-ignore:` の使用は 0 件）。

### 走査 2 —— submodule の中身に依存するジョブ

```console
$ grep -ln 'submodule update\|ai-stock-trading' *.yml
ci.yml
claude-code-review.yml
claude-coding.yml
codeql.yml
frontend-tests.yml
frontend.yml
images.yml
security.yml
```

**8 件**。`ci.yml` / `images.yml` / `security.yml` / `claude-*.yml` は `paths:` を持たない
（走査 1 に出ない）ため、bump でも起動する。

### 走査 3 —— `src/*` の glob を持つ行（gitlink 1 エントリに一致しない形の実体確認）

```console
$ grep -n 'src/\*/frontend\|src/\*\*\|"src/' *.yml
（frontend.yml 26 行・frontend-tests.yml 18 行・openapi.yml 1 行・images.yml のコメント 1 行）
```

`src/*/frontend/**` は `src/ai-stock-trading/frontend/**` に**一致し得る形**だが、本リポジトリに
その実体ファイルは追跡されておらず（submodule）、bump で変わるのは gitlink の `src/ai-stock-trading`
1 エントリのみである。よって一致しない。

### 交差と判定

走査 1 ∩ 走査 2 = **3 件**（`codeql.yml` / `frontend.yml` / `frontend-tests.yml`）。
走査 1 の残り 2 件（`copilot-setup-steps.yml` / `openapi.yml`）も個別に評価した。

| ワークフロー | 判定 | 理由 |
| --- | --- | --- |
| `frontend.yml` | **対象** | `push` / `pull_request` の**両方**に `paths:` があり、bump では build・`check-chunk-budget`・typecheck・lint・`check-static-egress` が一度も走らない |
| `frontend-tests.yml` | **対象** | 同上。横断 vitest は AST の feature テストも収集する（同ファイルのコメント）ため、bump でカバレッジ床の検査が素通りする |
| `codeql.yml` | **除外** | `paths:` は **`pull_request` にしか無く、`push`（develop / main）と `schedule` は全量解析のまま**である。bump は PR 時に解析されないが**マージ後の push で必ず解析される**ため、frontend 側のように**恒久的に素通りしない**。是正するなら「PR 時の解析の前倒し」という別の論点であり、#747 の射程（ratchet の素通り）外。**気づきとして §7 に残す** |
| `openapi.yml` | **除外** | `paths:` は `src/*/backend/**`。ただしジョブは submodule を取得せず、`scripts/generate-openapi.sh` は本リポジトリに存在しないため実行経路は `gen-openapi-skeleton.js`（既存を上書きしない）に落ちる。**AST の bump で結果が変わらない** |
| `copilot-setup-steps.yml` | **除外** | `paths:` は自ファイル 1 件のみ。submodule の中身に依存しない |

**該当 2 件 / 除外 3 件**（走査 1 の 5 件を全数評価した。黙って落としたものは無い）。

### 引き直しで新たに誤りになる自分の記述（規則 10）

`CLAUDE.md` の CI 節は `paths: ["src/*/frontend/**", ...]` を**例示**として引いているだけで、
一覧の網羅を宣言していない。よって本変更で誤りにはならない（追随不要）。
`docs/ai-workflow.md` の「必須チェックに指定する際の注意」は `frontend.yml` が `paths:` を
持つことを前提に「必須にしない」と書いており、**`paths:` を広げても前提は変わらない**
（依然として起動しない PR がある）。追随不要。

## 4. 設計

両ファイルの `push.paths` / `pull_request.paths`（計 4 箇所）へ次の 1 行を加える。

- 値は **`src/ai-stock-trading`**（末尾に `/**` を付けない）。bump で変わるのは gitlink の
  そのパス 1 エントリであり、`src/ai-stock-trading/**` では一致しないためである。
- `src/*` のような包括形は採らない。`src/Directory.Build.props` 等のバックエンド専用ファイルまで
  拾い、フロント CI を無関係な変更で起動させる。
- **ユニット追加時はここも足す必要がある**旨をコメントに残す（checkout 側の submodule 取得は
  `.gitmodules` から総なめする汎用形だが、`paths:` は glob で gitlink を表現できない）。

**起動条件・必須チェックへの影響**（`.github/workflows/` を変えたら確認する規約）:

- 変更は `paths:` への**追加のみ**。既存パターンの削除・`types:` の変更・ジョブ名の変更は無い。
  よって**起動する PR の集合は単調に広がるだけ**で、狭まる経路は無い。
- 両ワークフローは `paths:` を持つため**もともと必須チェックではない**（`docs/ai-workflow.md`
  「必須チェックに指定する際の注意」1 / IADR-0182）。追加後も `paths:` は残るため、
  必須チェック一覧は変わらない。
- `types: [opened, synchronize, reopened]` は不変。#705 の回帰テスト（`reopened` 必須）に影響しない。

## 5. 回帰の固定（検査器を足す判断）

`CLAUDE.md` の「検査器・規約の追加は同型の事故が 2 回起きたら」を満たす。**同型（`paths:` の
取りこぼしで検査が静かに素通りする）はこれが 3 件目**である。

1. **#558** — `frontend-tests.yml` に契約と生成の設定が無く、契約だけを直す PR でカバレッジ床の
   検査が起動しなかった（同ファイルのコメントに記録あり）。
2. **#562** — 整形ゲートの設定（`.prettierrc.json` / `.prettierignore`）が `paths:` に無く、
   単独変更で CI が走らなかった（`frontend.yml` のコメントに記録あり）。
3. **#747**（本件）。

固定するのは**一般形**とする: `.gitmodules` の `src/` 配下の submodule パスが、両フロント
ワークフローの `push` / `pull_request` 双方の `paths:` に列挙されていること。`.gitmodules` から
期待値を導出するため、**将来 `src/` 配下へ submodule を足したときも自動で赤くなる**。
走査 0 件で緑になる形（#664 / PR #672 の型）を塞ぐガードを併せて置く。

### ［2026-08-15 追記 / #747］この判断で波 1 の並列条件が破れた —— **本 PR は #749 の後にマージする**

#454 の棚卸し（`docs/specs/20260815_issue-454_open-issue-stocktake-and-waves.md`。**別ブランチ
`docs/nfr-open-issue-stocktake` にあり、本ブランチには実体が無いためリンクしない**）の §6 は、
**「#747 の回帰担保を `scripts/scripts.repo.test.js` に置くなら、#747 は #749 の後ろへ回す」**
という条件つきで #747 と #749 を波 1 の並列に置いていた。**本作業はまさに同ファイルへ 66 行を
足したため、この条件に該当した。**

- **並列可否は宣言済みファイル領域の非重複で機械的に判定する**（運用ガイド）。#749 も
  `scripts/scripts.repo.test.js`（`:6085-6180`）に書くため、**領域が交差する**。
- **したがって本 PR は #749 の後にマージする。** FIFO で 1 本ずつ（#749 を develop へマージ →
  本 PR を rebase → CI 通過 → マージ）。**並列に進めてよいのは実装作業までで、マージ順は直列である。**
- **宣言せずに黙って同ファイルへ書くのが事故である**（条件が付いていたのに、該当したことを
  誰も知らないまま 2 本が同時にマージ待ちになる）。**該当した時点で本書に書く。**

### 足した検査と、既存の「`paths:` の側は検査器にしない」注記との射程の違い

`scripts/scripts.repo.test.js:4300`（#705 / IADR-0182 のブロック）は「**`paths:` の側は検査器に
しない** —— `frontend.yml` 等は意図して `paths:` を持ち、必須にしないことで正しく運用されている。
機械的に禁じると正当な設定を壊す」と述べている。**本 PR の検査はこれに抵触しない**:
**あちらは `paths:` を持つこと自体の禁止（存在の禁止）を退けたもので、本検査は `paths:` を持つ前提で
その列挙に `src/` 配下の gitlink が入っているかだけを見る（列挙の要求）。存在の禁止 ≠ 列挙の要求**であり、
本検査は `paths:` の有無にも required 化の可否にも触れない。同趣旨を検査ブロックのコメントにも残した。

## 6. 受け入れ基準

- [x] `frontend.yml` の `push.paths` と `pull_request.paths` の**両方**に `src/ai-stock-trading` がある
- [x] `frontend-tests.yml` の `push.paths` と `pull_request.paths` の**両方**に `src/ai-stock-trading` がある
- [x] 既存の `paths:` エントリ・`types:` ・ジョブ名・ステップに変更が無い（起動条件は広がるのみ）
- [x] `.gitmodules` の `src/` 配下 submodule が両ワークフローの 4 箇所に列挙されていることを
      `scripts/scripts.repo.test.js` が検査し、`node scripts/scripts.test.js` が通る
- [x] `node scripts/check-action-versions.js` / `node scripts/check-ai-workflow-config.js` が通る
- [x] 案 B は実装せず、PR 本文で別 issue への切り出しを提案する
- [x] **回帰テストのコメントとテスト名に起点 ID（無採番 `NFR`）が入っている**（ワークフロー側の
      コメントは `NFR / issue #747` で入っていたが、テスト側が `#747 / #558 / #562` だけで
      片側欠けていた。`.claude/rules/traceability.md`「テスト名またはコメントに起点 ID を残す」）
- [x] **`scripts/scripts.repo.test.js` へ書いたことで #454 §6 の条件に該当したため、
      #749 の後にマージすると §5 に明記した**

## 7. テスト方針

CI の起動条件は本番の GitHub 上でしか実走できないため、**設定の静的検査**へ写像する
（`scripts.repo.test.js` に §5 の一般形を置く）。実起動の確認は、本 PR 自身が
`.github/workflows/frontend*.yml` を変更するため両ワークフローの `paths:` に含まれる
`.github/workflows/frontend.yml` / `frontend-tests.yml` に一致し、**PR の CI 上で
両ワークフローが skipped ではなく起動していること**で見る（#524 / #558 の先例どおり、
「success か」ではなく「skipped になっていないか」を見る）。

## 8. 計画書との差異

- 差異: なし（本作業は工程側のメタ作業であり、計画書の記述を変更しない）。

## 9. 気づき・未決事項（PR 本文へ持ち出す）

1. **案 B は別 issue へ切り出す。** AST features の静的 import を遅延 import へ変えると、
   AST の成長が基盤の初期ロードを押し上げなくなる。ただし `featureRegistry` の合成方式に
   手が入り **IADR-0134 の分割境界の改定**にあたるため、IADR を伴う別 issue が要る。
   案 A（検査の穴を塞ぐ）と案 B（設計の是非）は排他ではない。
2. **`codeql.yml` の PR 時解析も submodule bump では起動しない**（母集合の除外 3 件のうち 1 件）。
   `push` / `schedule` が全量解析のため恒久的な素通りにはならないが、**AST 由来の SAST 指摘が
   マージ後まで出ない**。是正の要否は別途判断する。
3. 案 A の副作用として **submodule bump のたびにフロント CI が回る**（実測 5 日で 3 回）。
   これは意図した挙動である。
