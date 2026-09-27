---
title: "T-25 の偶然の赤を同じコミットの 1 回の再実行で確かめ（T-25 だけの赤なら自動）、p の分布を月次で要約する（#1617）"
type: spec
status: done
related_ids: [SC-15, NFR-13, ADR-0118, ADR-0113, ADR-0097, ADR-0094, IADR-0470, IADR-0232, IADR-0432]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0118_timing-chance-red-rerun-and-monthly-record.md 決定 1〜5・フォローアップ 1〜3
  - planning:projects/microservices-platform/07_adr/ADR-0113_timing-judgement-rank-sum-test.md 決定 1・2・§結果・フォローアップ 2
  - planning:projects/microservices-platform/07_adr/ADR-0097_timing-floor-release-default-on-and-periodic-review.md 決定 1（2026-09-27 改訂）・決定 3
  - planning:projects/microservices-platform/10_feedback/20260927_timing-chance-red-rerun.md
---

# 仕様書: T-25 の偶然の赤の 1 回の再実行（自動）と、p の分布の月次の要約（#1617）

## 起点となる計画書（トレーサビリティ）

- 画面: **SC-15**（パスワードリセット。テスト仕様書の T-25 が所要時間の軸）／非機能要件: **NFR-13**
- 計画 ADR: **ADR-0118**（本作業の直接の起点。決定 2 = 赤はすべて起票・確かめの最初の手順は同じコミットの 1 回の再実行・再実行は 1 回まで／
  決定 3 = ADR-0097 決定 1 の 7 日の窓と「統制が破れた」は偶然の赤を数えない／決定 4 = 月次の記録の 6 項目と環流の条件／決定 5 = 3 点セット／
  フォローアップ 1〜3）／**ADR-0113**（判定の値。有意水準 1%・両側・片側 12・反復 3。変えない）／**ADR-0097**（決定 3 = 検査器の赤を床の引き直しの契機とする）／ADR-0094
- 実装 IADR: **IADR-0470**（順位和検定の実装。本作業で追記）／IADR-0232（CI の失敗の自動起票。変えない）／IADR-0432（床）
- 起票: #1617（planning#681 の裁定の実装側の残作業）

## 目的・背景

planning#681 の裁定（ADR-0118）は、T-25 の偶然の赤（系統差が無くても約 1%）を「同じコミットで 1 回だけ再実行し、合格なら偶然の赤として記録して閉じる。
また赤なら床の引き直しの契機」とし、p の分布を月次で実装側へ記録するよう定めた。ADR-0118 決定 5 は「再実行・偶然の赤の記録・月次の要約は無い」と書き、
フォローアップ 1〜3 が実装を求めている。

## 🔴 着手前に確認した制約

| 制約 | 出典 | 本作業での扱い |
| --- | --- | --- |
| 判定の値（1%・両側・片側 12・反復 3）は変えない | ADR-0118 決定 1 / ADR-0113 決定 1・2 | 判定関数は 1 行も変えない。足すのは「失敗が T-25 の順位和検定の赤ただ 1 件か」の分類と手順の出力だけ |
| 赤はすべて起票する。起票の条件を置かない | ADR-0118 決定 2 / ADR-0097 決定 3 の「一律の契機」 | `ci-failure-issue.yml` と `report-failure` は変えない。再実行は起票の**後**に起こす |
| 再実行は赤の実行ごとに 1 回まで | ADR-0118 決定 2 | attempt 1 だけを再実行の対象にし、attempt ≥ 2 では再実行しない（ジョブの `if:` と script の両方） |
| 他の門も赤なら偶然の赤と扱わない | #1617 のやること 1 | 検査器の出力・候補の手順の `if:`・script の手順の数え直しの 3 重 |
| マーカーはワークフロー単位（本物の失敗も同じ issue に集まる） | ADR-0118 実測 3・§結果 | **issue は自動で閉じない**。閉じる前の確かめを運用文書へ書く |
| 稼働クラスタに触れない・ワークフローを起こさない | 依頼の制約 | 月次の集計は GitHub の API の GET だけ。自動の再実行の実地確認は利用者の判断に残す |

## 設計（検討した選択肢）

### 再実行の手段

| 案 | 評価 |
| --- | --- |
| 1. **`workflow_run: completed` で起動する別ワークフローから `gh run rerun <id> --failed`**（採用） | ✅ **同じ run の新しい attempt ＝同じコミット・同じワークフロー定義**が構造的に保証される。`run_attempt` で「1 回まで」を判定できる。GITHUB_TOKEN の `actions: write` で足りる |
| 2. integration-stack.yml の中の後続ジョブから `workflow_dispatch`（SHA を入力で渡して checkout） | ❌ dispatch は ref しか取れず、ワークフロー定義は develop の先頭になる（同じ定義を測る保証が無い）。「再実行の再実行」を入力で持ち回る必要がある |
| 3. 同じ run の中から自分を再実行 | ❌ 実行中の run は再実行できない（API が拒否する） |
| 4. 検出とコメントだけ（手の再実行） | 暫定手段（ADR-0118 決定 5）と同じ。案 1 が成り立つので採らない。**案 1 が失敗したときの退路**として、手で打つコマンドを issue へ書く |

### 「T-25 だけの赤」の判定の置き場

- パスワードリセットの門（1 手順）は T-10 / T-16 / T-17 / T-20 / T-25 をまとめて測るので、**手順の結論だけでは T-25 だけの赤か分からない**。
  → 検査器（`check-password-reset-mail.js`）が `isTimingOnlyRankSumRed` で「失敗が順位和検定の `不合格` ただ 1 件か」を判定し、手順の出力 `t25_only_red` に書く
  （`評価不能`・判定の前提の不合格・申請を通せなかった標本・他の項目の失敗が混ざれば false）。
- integration-stack.yml に**判定しない印の手順**「T-25 only red (chance-red candidate)」を置き、`if:` で「検査器の出力が true ∧ ほかの門と 2 つの投入がすべて success」を課す。
  別ワークフローは API で手順の結論を読む（ジョブの出力は API に出ない）。
- 別ワークフローの script（`t25-rerun-on-chance-red.js`）は、印の手順が success であることに加えて**手順の結論を数え直す**
  （失敗した手順がパスワードリセットの門ただ 1 つ・ほかの門がすべて success・ほかのジョブが赤でない）。`if:` の式が崩れても他の門の赤を再実行しない。

### 月次の要約

- 置き場は IADR-0470 への日付つき追記（ADR-0118 決定 4 の例示どおり。新しい IADR は作らない）。
- 数えは `scripts/t25-monthly-summary.js` が行う（手で数えない）。**観測の単位は attempt**（再実行も帰無仮説のもとで独立な 1 標本）。
  偶然の赤＝赤の観測の同じコミットの次の観測が `合格`／再実行も赤＝次の観測も `不合格`（環流）／再実行なし＝次の観測が無い。
- ログの区間は `##[group]Run node scripts/check-password-reset-mail.js`（`--self-test` を除く）から次の `##[group]Run` まで。🔴 **`--live` を要件にしない**
  （#1550 より前の run は引数なしで呼んでいた。初稿は `--live` を要件にして 12 件を「届かず」に落とした。実データで見つけて直し、自己試験に加えた）。
- 除外: 判定式の変更前（run 36217595485 の作成時刻より前）・届かない attempt・床の無い比較実行（手順の env の `ISTIO` が空）・判定の前提の不合格（p が出ない）。

## 母集合（[[IADR-0141]] 決定 1・規則 9・10。着手時に自分で引いた）

**軸 1（誤りの側の文字列）**: `片側 ?6|20 通|反復 3 ?[×x] ?片側` を追跡下の全ファイル（`src/ai-stock-trading/**` を除く）で引いた（Grep・拡張子で絞らない）。

| ヒット | 扱い |
| --- | --- |
| `.github/workflows/integration-stack.yml`（「メールを 20 通作る」「反復 3 × 片側 6 標本」） | **直した**（37 通・反復 3〔暖機 1〕× 片側 12・まとめて片側 24・両側・有意水準 1%）。旧記述は経緯として 1 行残した |
| `.ai-context/adr/IADR-0432_*`（L141・L180・L222・L373） | 除外: 確定済みの IADR の当時の記述。L373 の追記が「片側 6 → 12」を既に記録している |
| `.ai-context/adr/IADR-0470_*`（L127「変異 … 片側 6」） | 除外: 変異試験の変異の名前（現行の値ではない） |
| `.ai-context/specs/` の 1410 / 1470 / 1525 / 1546 / 1541 | 除外: 確定済みの作業仕様書（書き換えない） |
| `docs/tests/SC-17_*`（「20 通り」）・`.ai-context/specs/` 779 / 1596（「20 通り」） | 除外: 別の意味（変異の数） |
| `docs/operations/password-reset-relay-state-measurement-runbook.md` L472・L870 | 現行の値（片側 12・36 通）。変更なし |

**軸 2（自分の変更で新たに誤りになる記述・規則 10）**: `permissions` の表（`scripts.repo.test.js` の `JOB_SCOPES`・`docs/ai-workflow.md` の表）に新しいジョブを載せた。
検査器の母集合の件数固定（58）には新しい 2 本を `NOT_CHECKERS`（集計器・実行器）として加え、件数は変えていない。
`scripts/README.md` の CI の表と使い方、`docs/operations/operations.md` の障害対応へ手順を足した。

**軸 3（起動条件・必須チェック）**: integration-stack.yml の契機（schedule / push(develop) / dispatch）とジョブは変えていない（手順を 1 つ足し、3 手順へ `id:` を足しただけ）。
新しい `integration-stack-rerun.yml` は `workflow_run` だけで起動し、PR では走らない → **必須チェックの表（`docs/ai-workflow.md`）は変わらない**。
`ci-failure-issue.yml` の呼び出し側は 6 本のまま（新しいワークフローは呼ばない）。

## 変更内容

| ファイル | 変更 |
| --- | --- |
| `scripts/check-password-reset-mail.js` | `run()` が `timing` を返す。純関数 `isTimingOnlyRankSumRed` / `timingStepOutputs` を足し、main が `$GITHUB_OUTPUT` へ `t25_only_red` / `t25_p` / `t25_w` を**合否の前に**書く。自己試験 +4（54 → 58） |
| `.github/workflows/integration-stack.yml` | 古い注記を直した。門 3 つへ `id:`（`reset-mail` / `abac-search-gate` / `login-disclosure`）。印の手順「T-25 only red (chance-red candidate)」 |
| `.github/workflows/integration-stack-rerun.yml`（新設） | `workflow_run: completed`。ジョブの `if:` = 契機 push/schedule ∧（attempt 1 ∧ failure ∨ attempt 2）。`actions: write` / `issues: write` |
| `scripts/t25-rerun-on-chance-red.js`（新設） | 判定 `decide`・分類 `rerunOutcome`・文面・実行（`--apply` のときだけ `gh run rerun --failed` と issue へのコメント）。二重の抑止（最新の attempt が 1・記録のマーカー）。自己試験 16（監査 R1 で 17） |
| `scripts/t25-monthly-summary.js`（新設） | ログの読み取り・振り分け・偶然の赤の判定・KS 距離・要約の文面・GET だけの読み取りの器。`--month` / `--json` / `--self-test`（16） |
| `scripts/scripts.repo.test.js` | #1617 節（自己試験 2 本・印の手順の `if:` の場面ごとの評価・再実行のワークフローの真理値表・rerun であって dispatch でないこと・古い注記）。`JOB_SCOPES` と `NOT_CHECKERS` |
| `docs/operations/operations.md` | 障害対応へ「偶然の赤の確かめ方と月次の記録」（閉じる前に同じ実行の他の門と issue の他の実行の失敗を確かめる） |
| `docs/ai-workflow.md` / `scripts/README.md` | 権限の表・CI の表・使い方 |
| `.ai-context/adr/IADR-0470_*` | 2026-09-27 の追記（自動の再実行の設計・月次の要約 2026-09 の 1 回目） |

## 受け入れ基準

- [x] T-25 だけが赤のときに 1 回だけ再実行される（`decide` の場面・`execute` の呼び出し回数・印の手順の `if:` の場面 1・ジョブの `if:` の真理値表）
- [x] 他のゲートも赤のときは、再実行で偶然の赤として閉じない（印の手順の場面 4〔5 手順 × failure / skipped / cancelled〕・script の数え直し・issue を自動で閉じない）
- [x] 再実行の再実行はしない（attempt ≥ 3 はジョブが起動しない・`decide` が none・attempt 2 では rerun を呼ばない）
- [x] どちらも試験（ワークフローの `if:` の評価。#1599 の枠組み）で固定されている（`scripts.repo.test.js` の #1617 節）
- [x] integration-stack.yml の注記が ADR-0113 / ADR-0118 の値
- [x] 月次の要約を 1 回分、実際に出せる（2026-09 分を IADR-0470 へ追記。#1597 が手で数えた 21 件の部分集合を再計算して一致を確かめた: 中央値 0.5062・D 0.1335・p < 0.5 は 10/21）
- [x] 閉じる前の確かめ（同じ実行の他の門・issue の他の実行の失敗）を運用文書に書いた

## 検証（証跡）

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → `✓ 839 tests passed`（#1617 の 6 件を含む）
- `node scripts/t25-monthly-summary.js --self-test` → 16 件 OK／`node scripts/t25-rerun-on-chance-red.js --self-test` → 16 件 OK／
  `node scripts/check-password-reset-mail.js --self-test` → 58 件 OK
- 実データ（読むだけ）: `node scripts/t25-rerun-on-chance-red.js --run-id 36244009369 --attempt 1` → `none`（印の手順が無い古い定義）／
  `--run-id 36253517299 --attempt 1` → `none`（緑）
- 変異試験（実装のコミットの上で当て、`git show HEAD:<path> > <path>` で戻した）:
  1. `decide` の `run.run_attempt === 1` を `>= 1` へ（attempt 2 が再実行の分岐へ入る＝再実行の再実行）→ 自己試験
     「再実行の再実行はしない」が `actual: 'rerun' / expected: 'report'` で落ち、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` も exit 1
  2. integration-stack.yml の印の手順の `if:` から `&& steps.login-disclosure.outcome == 'success'` を外す → #1617 節が
     「🔴 Gate — ログイン経路の存在秘匿（#1245 PR-0） が failure なのに候補になる」で落ち、exit 1
  - 戻した後: `✓ 839 tests passed`・`git status` は空
- `k8s-local-up.test.js`（#1597 の試験。本作業で足した `id:` の影響）は、稼働クラスタ用の起動器をスタブの下で走らせる試験のため手元では走らせず、PR の CI（`static-checks`）の結果で確かめる

## ［2026-09-27 追記 / #1617］監査 R1: 起動元のブランチとリポジトリの多層防御

PR #1623 の監査（GO）の R1 を同じ PR に足した。**判定の筋は変えていない**（自動で扱う run を狭めただけ）。

- `integration-stack-rerun.yml`: `on.workflow_run` に `branches: [develop]` を足した（head が develop の run だけで起動）。
  ジョブの `if:` にも `head_branch == 'develop'` と `head_repository.full_name == github.repository` を足した（真理値表で試験できる形にするため）。
- `t25-rerun-on-chance-red.js` の `decide`: `run.head_branch === 'develop'` と `run.head_repository.full_name === run.repository.full_name` を要件にした。
  どちらも script が既に読んでいる attempt の run オブジェクト（`GET /actions/runs/{id}/attempts/{n}`）にある（run 36244009369 で実測: `develop` / `endazon/microservices-platform` / `endazon/microservices-platform`）。
  読めない（null・空）ときも扱わない（fail-closed）。
- 試験: 自己試験 +1（ブランチ main / feature/x / 空 / 無し × attempt 1・2、head のリポジトリがフォーク・null・空、基のリポジトリが null → すべて none。16 → 17 件）。
  `scripts.repo.test.js` の真理値表に `branches: [develop]` の存在と、ブランチ 4 種・フォークの head × 3 場面で起動しないことを足した。
- 変異試験: `decide` の `if (run.head_branch !== RERUN_BRANCH) return none(…)` の 1 行を消す → 自己試験が
  「ブランチ feature/x」で落ち（exit 1）、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` も exit 1。`git show HEAD:<path> > <path>` で戻し、
  自己試験 17 件 OK・`✓ 841 tests passed`・`git status` は空。
- 実データ（読むだけ）: run 36244009369 attempt 1 はブランチとリポジトリの確かめを通り、従前どおり「印の手順が無い」で none。

## 残るもの

- 🔴 **自動の再実行は、実際の赤でまだ 1 度も動いていない**（PR の CI では `workflow_run` を起こせない）。`gh run rerun --failed` を GITHUB_TOKEN で呼べること・
  再実行の attempt の完了で再び `workflow_run` が届くことは、GitHub の仕様に拠っている。確かめるのは次の T-25 の赤（平均して約 9 日に 1 回）か、
  利用者が判断する検証の起動である。届かなかったときの退路（手のコマンドを issue へ書く・ジョブを赤にする）は用意した。
- 月次の要約は毎月手で `t25-monthly-summary.js` を回して IADR-0470 へ追記する（定期実行にはしていない）。
