---
title: 作業仕様書 — 統合テストが 1 件も実走しなかった回収実行を赤にし、全 skip の緑を区別する（#1788）
type: spec
status: done
related_ids: [NFR, ADR-0007, ADR-0090, IADR-0232, IADR-0414, IADR-0507]
author: claude
created: 2026-10-08
updated: 2026-10-08
issue: "#1788"
---

# 作業仕様書 — 統合テストの「全 skip で緑」を区別する（#1788）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。基点は MSP `origin/develop` `2f2aa957`。
> 計画は project-planning `b5b584f`（隣接クローン・読み取り専用）の ADR-0090 を読んだ。
> 先例として AST#1200（AST `cbc8fa5e`。`scripts/check-integration-skips.js`・AST の IADR-0497）を読んだ。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0090 決定 3**（planning#575 の裁定）／ADR-0007（CI）。決定 1（門は依存ごとに訊いて skip）は IADR-0414 で着地済み。
- 非機能要件: 無採番（CI の門。メタ作業）。ADR-0090 自身が「本件に当たる要求 ID は無い」と書く。
- 台帳: #1772 の行「ADR-0090 決定 3」。

## 受け入れ基準

- AC1: **Given** 回収実行で依存（Postgres・ブローカ・Qdrant 等）が 1 つも得られない **When** 統合試験が skip する **Then** job は失敗し、理由に「実走 0 件」または「依存を得られない skip」が件数つきで出る。
- AC2: **Given** 依存が揃った通常の実行 **When** 統合試験が実走する **Then** job は従来どおり成功し、ユニットごとの宣言・実走・skip が summary に出る。
- AC3（否定形）: ローカル（依存の無い開発機）での skip の挙動は変えない（試験コード・門のコードに触れない）。
- AC4: 検査は TRX・一覧の欠落・破損を「0 件」と読まず赤にする。
- AC5: 起動条件・ジョブ名・必須 check は変えない。実行に `--filter` を足さない（IADR-0232 改定 3）。
- AC6: 検査器を変異させると自己試験が落ちる（3 件以上）。

## 母集合（規則 9・10。`2f2aa957` 時点）

### 規則 9（全 skip で緑になり得る統合・E2E の job を走査）

走査: `.github/workflows/*.yml` のうち `dotnet test` / `playwright test` / `test:e2e` / 統合スタックの検査を含むもの
（`grep -ln 'dotnet test\|playwright\|e2e' .github/workflows/*.yml` → `ci.yml` `claude-code-review.yml` `claude-coding.yml` `frontend-tests.yml` `frontend.yml` `integration.yml`）と、名前に integration を含む `integration-stack.yml` `integration-stack-rerun.yml`。

| job | 形 | 扱い |
| --- | --- | --- |
| `integration.yml` `integration` | 全量の `dotnet test`（全ユニット）。統合試験は門で skip し得る | **対象** |
| `ci.yml` `build-and-test` ほか | `--filter "Category!=Integration"`。統合試験を含まない | 除外 E1 |
| `frontend.yml` `e2e` | Playwright。`test.skip` / `fixme` は `src/platform/frontend/e2e` に 0 件。試験 0 件は Playwright 自身が失敗にする | 除外 E2 |
| `frontend-tests.yml` | Vitest の単体 | 除外 E1 |
| `integration-stack.yml` / `integration-stack-rerun.yml` | `dotnet test` ではなく検査器（`check-stack-ready.js --live` 等）が稼働スタックを直接測る。skip の概念が無い | 除外 E3 |
| `claude-code-review.yml` / `claude-coding.yml` | 一致は AI への指示文の中の文字列 | 除外（試験を走らせない） |
| submodule のユニット `ai-stock-trading`（`integration.yml` の glob に入る） | AST の統合試験も同じ実行で走る | 除外 E4 |

- **E1**: 統合試験（`Category=Integration`）を走らせない job には「全 skip」が起きない。
- **E2**: 動的 skip が 0 件で、試験 0 件の実行は既に赤になる。動的 skip を足す変更が来たら見直す。
- **E3**: 判定は検査器の live 測定であり、skip で緑になる経路が無い（`integration-stack.yml` 冒頭の「起きていないのに緑にしない」が同じ問題を既に扱っている）。
- **E4**: MSP の検査器は submodule を対象外にする（`lib/excluded-units.js`）。AST の `integration.yml` が AST#1200 で同じ守りを持つ。

### 規則 10（この変更で新たに誤りになる記述）

`grep -rn 'integration\.yml' docs .ai-context/adr/IADR-0232* .ai-context/adr/IADR-0414* scripts/README.md` と `全 skip|skip 件数|ADR-0090` の走査:

| 箇所 | 扱い |
| --- | --- |
| `docs/ai-workflow.md` 床の節「配備済みの手段と暫定手段」 | 床が全 skip を止めないことと本検査を 1 項足す |
| `docs/tests/TEST_STRATEGY.md` の `Category` 行（「回収実行は全量」） | 回収実行で実走を数えることを足す |
| `scripts/README.md` | 行を足す |
| `scripts/scripts.repo.test.js` の検査器の母集合（59 本） | 60 本へ（ラチェットの発火） |
| `.ai-context/adr/IADR-0232` / `IADR-0414` | 凍結記録。書き換えない（本 IADR が前提として引く） |
| `.ai-context/specs/20261007_1772_residual-ledger.md` の行 6（「門は無い」） | 確定済み仕様書。書き換えない（台帳 #1772 側で閉じる） |
| `integration.yml` の「3. fail-closed の門が要らなくなる」 | 本検査とは別物（`--filter` 由来の 0 件）。新しいコメントで区別を書いた |

## 設計（決定は IADR-0507）

1. `integration.yml` の試験 step: `dotnet test` に `--logger trx` を足す。続けて `--list-tests --filter "Category=Integration"` の出力を `$RUNNER_TEMP/integration-lists/<unit>.list` へ残す（失敗しても出力ごと残し、検査器が赤にする）。
2. `scripts/check-integration-executed.js --lists <dir>`: ユニットごとに一覧 × TRX を突き合わせ、宣言のあるユニットで実走 0 件、依存不足の skip ≥ 1、一覧なし、TRX 破損、宣言が全体で 0 件のいずれかで赤。表を summary へ出す。
3. 自己試験 step と本検査 step を床の後ろに置く（`!cancelled() && steps.tests.outcome != 'skipped'`）。
4. `scripts.repo.test.js`: 自己試験・配線（`--logger trx`・一覧・2 step・実行の行に `--filter` が無いこと・条件つき step 5 本）・目印が門の実装に在ること・README 記載。

## 検証（証跡）

### 実データ（ローカル。Docker の daemon には届かない環境）

- **全 skip 相当（AC1）**: `unshare -m sh -c "mount -t tmpfs none /var/run && env -u CI -u DOCKER_HOST dotnet test src/knowledge/backend/backend.slnx --no-build -c Release --logger trx"` → 12 プロジェクトすべて `Passed!`。続けて `node scripts/check-integration-executed.js --lists <dir>`（一覧は `dotnet test src/knowledge/backend/backend.slnx --no-build -c Release --list-tests --filter "Category=Integration"` の実出力）→ **exit 1**:
  `| knowledge | 59 | 1 | 1 | 0 | 58 | 57 | 0 | 0 | 12 |`・`::error::knowledge: 「依存を得られない」で skip された統合試験が 57 件（上限 0 件。実走 1 件）`。**`dotnet test` は緑なのに門が赤**＝区別できている。
- **実走あり（AC2 の判定側）**: ソケットが在る状態で `Knowledge.IntegrationTests` を走らせる（daemon に届かず 59 件が失敗＝実走）→ `| knowledge | 59 | 59 | 0 | 59 | 0 | 0 | 0 | 0 | 1 |`・**exit 0**（合否はテスト step が判定する）。依存が揃った実行での合格は PR 後の回収実行で確かめる（下記）。

### 自己試験と変異（AC6）

`node scripts/check-integration-executed.js --self-test` → 20 件 OK。各変異で自己試験が落ちることを確かめた（10 件すべて killed）:
依存不足 skip の判定を外す／実走下限を 0 に／一覧の見出し判定を常に真に／壊れた TRX を読み飛ばす／`TestResults/` 外の TRX も拾う／目印の照合を常に偽に／全体の宣言 0 件判定を外す／対象外ユニットを判定に入れる／一覧との突合を外す（単体試験の実走で埋める）／Theory の引数落としを外す。
初回は 3 件が生き残った（見出し判定・`TestResults/` 制限・突合）→ 試験の入力を「他の判定に紛れない形」に直して殺した。

### CI

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → 953 件 OK。
- `actionlint .github/workflows/integration.yml` → 指摘なし。起動条件（push: develop・schedule・workflow_dispatch）とジョブ名は不変。
- `integration.yml` は PR では起動しない。PR の枝で `workflow_dispatch` を起こし、依存が揃った実行で門が緑になり表が出ることを確かめる（結果は PR に書く）。
- ［2026-10-08 追記 / #1788］初回の dispatch（run 37657656611）は **本検査は合格**（`| knowledge | 59 | 58 | 58 | 0 | 1 | 0 | 0 | 0 | 12 |`・`| platform | 0 | …`）だったが、**床の件数突合が「レポート 38 件 / 期待 19 件」で落ちた**。原因は TRX ロガーが Cobertura を `TestResults/<run>/In/<machine>/` へ複製すること（ローカルで再現）。試験の直後に `TestResults/*/In` を消す 1 行を足し、`scripts.repo.test.js` で固定した（IADR-0507 決定 1）。床の値・判定器は変えていない。
