---
title: check-unit-dependencies.js のコメント「Shared の 2 プロジェクト」を IADR-0117（3 プロジェクト）へ追随させる
type: spec
status: done
related_ids: [NFR, IADR-0056, IADR-0057, IADR-0115, IADR-0117]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ./20260804_issue-478_staged-policy-citation-fix.md
  - ./20260803_issue-455_backend-application-standard.md
  - ./20260711_issue-231_unit-dependency-guard.md
  - "../adr/IADR-0117_platform-shared-kernel-placement.md"
---

# 仕様書: `check-unit-dependencies.js` のコメントを IADR-0117 の 3 プロジェクトへ追随させる

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性 — 検査器のコメントが旧制約値のままだと、読んだ実装者が
  `Platform.Shared.Kernel` を「許されていない参照先」と誤読する）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR:
  [IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md)（ユニット外参照の 2 → 3 改定。**是正後の正**）／
  [IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md)（決定 3。IADR-0117 が部分改定した被改定側）／
  [IADR-0057](../adr/IADR-0057_unit-dependency-machine-check.md)（本スクリプトの方式根拠）／
  [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)（キット由来ファイルの分類。編集可否の判断根拠）
- 先行判断の一次情報:
  [20260804_issue-478](./20260804_issue-478_staged-policy-citation-fix.md) 「据え置き」表
  （本件 3 箇所を #478 のスコープ外として記録し、別 issue 候補としていた）
- 規約: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
- 本リポジトリの起点: #484（検出元 #478 のクロス監査 / 親 #454）

## 目的・背景

[IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md) は
[IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md) 決定 3 を部分改定し、
ユニット外から参照可能な `src/platform/backend/Shared/` のプロジェクトを **2 → 3**（`Platform.Shared.Contracts` /
`Platform.Shared.Infrastructure` / `Platform.Shared.Kernel`）とした。同 IADR は「機械検査は
`^src/platform/backend/Shared/` の**パス接頭辞**で許容判定を行うためスクリプト変更は不要であり、
**改定で更新が要るのは件数を書いた文書だけ**」と明記している（理由 4 番目）。

その「件数を書いた文書」のうち `scripts/README.md` は PR #483（#478）で是正済みだが、
`scripts/check-unit-dependencies.js` 自身のコメントは旧値のまま残った。#478 の作業仕様書は本件を
「スコープ外として記録に留める（別 issue 候補）」としており、その別 issue が #484 である。

## 対象範囲

### grep による全量洗い出し（実測 / `origin/develop` = `f2d791d`）

issue は「行 11 / 45 / 96 付近」を挙げるが、件数表記の見落としを避けるため
`プロジェクト` を含む行を全量走査した。

```
grep -n 'プロジェクト' scripts/check-unit-dependencies.js
grep -rn '2 プロジェクト' --exclude-dir=.git --exclude-dir=node_modules .
```

| # | 行 | 現在の記述 | 扱い |
| --- | --- | --- | --- |
| 1 | 5（ヘッダの ID 列挙） | `IADR-0027 / IADR-0056 / IADR-0057` | 是正（IADR-0117 を追加。本文で同 ID を引くため） |
| 2 | 11（ヘッダの規則説明） | 参照先が `platform/backend/Shared/`（Contracts / Infrastructure）なら許可 | 是正（**2 つしか列挙していない**＝件数表記の同型） |
| 3 | 45（`isSharedProject` の説明） | ユニット外から参照を許可する **2 プロジェクト** | 是正 |
| 4 | 96（違反 `reason` 文字列） | ユニット外参照は … の **2 プロジェクト**のみ許可 | 是正 |

`プロジェクト` を含む残り 3 行（12 / 50 / 62・「Tests プロジェクト」「BFF エンドポイントプロジェクト」）は
件数表記ではなく誤りが無いため触らない。

### 洗い出しで見つかった「本 issue では是正しない」箇所と理由（据え置き判断）

| ファイル / 行 | 内容 | 据え置きの理由 |
| --- | --- | --- |
| [`scripts/check-unit-dependencies.js`](../../scripts/check-unit-dependencies.js) 269 | 実行時の案内 `依存規則は src/README.md「依存規則」/ IADR-0027 / IADR-0056 を参照してください。` | 件数表記ではなく、参照先として挙げた `src/README.md` は既に 3 プロジェクトへ是正済みなので**誤りが無い**。issue の名指しにも無い（過剰修正を避ける） |
| [`src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/Knowledge.Bff.Endpoints.csproj`](../../src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/Knowledge.Bff.Endpoints.csproj) 12 | `ユニット外は Shared の 2 プロジェクトのみ許可。` | IADR-0117 に未追随の**同型の残り**だが、issue #484 のスコープは `check-unit-dependencies.js` のコメントのみ。別 issue 候補として記録する（#478 が本件をそう扱ったのと同じ作法） |
| [`docs/tech/tech-requirements.md`](../tech/tech-requirements.md) 126、[`src/README.md`](../../src/README.md) 77、[IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md) 76 / 83、[IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md) 47 / 62 / 125 / 134、[`docs/adr/README.md`](../adr/README.md) 143 | 「**改定前は** 2 プロジェクトだった」という**経緯としての 2** | 是正すると改定の記録が壊れる。現行値としての誤りではない |
| 過去仕様書（[20260711_issue-231](./20260711_issue-231_unit-dependency-guard.md) 58、[20260710_FR-14](./20260710_FR-14_repo-restructure-platform-knowledge.md) 106、[20260803_issue-455](./20260803_issue-455_backend-application-standard.md) 101 / 199、[20260804_issue-478](./20260804_issue-478_staged-policy-citation-fix.md) 108 / 132） | 当時の事実としての 2 プロジェクト | 記述時点で正しく、IADR-0117 の経緯を語る文脈でもある。#478 の作法（当時の事実は消さず誤りだけを直す）に照らして誤りが無い |

### 含まないもの

- **コード動作の変更**。判定ロジック・`module.exports`・自己試験ケースは一切触らない。
- 上記「据え置き」表の各行。
- `Platform.Shared.Kernel` の実体作成（IADR-0117 フォローアップ 1。最初にそれを必要とするサービス再実装 issue が担う）。

## 設計

### ロジックに件数依存が無いことの確認（実測）

IADR-0117 理由 4 番目の主張を、コードで再確認した。

- `isSharedProject()`（45–48 行）は `/^src\/platform\/backend\/Shared\//` の**パス接頭辞正規表現**のみで判定し、
  プロジェクト名の列挙も件数の比較も持たない。`Platform.Shared.Kernel` が実体化すれば無変更で許容される。
- `classifyProjectReference()`（70–98 行）で件数に触れるのは 96 行の `reason` **文字列**だけで、
  分岐条件には現れない。
- 96 行の `reason` は違反時のメッセージであり、`scripts/scripts.repo.test.js`（123–190 行）も
  自己試験（`--self-test`）も `.ok` の真偽しか assert しない。文言変更で落ちるテストは無い（実測で確認）。

したがって**ロジック側に件数依存は無く**、本作業はコメント（および違反メッセージの文言）に閉じる。

### 是正後の文言

`scripts/README.md` 13 行目の是正後文言（PR #483）に揃える。

> `platform/backend/Shared/` の **3 プロジェクト**のみ許可 …（2 → 3 の改定は IADR-0117）

3 プロジェクトを列挙する箇所では、IADR-0117 決定 4 のとおり **`Platform.Shared.Kernel` が未作成**である
ことも併記する（コメントの「3」と `ls` の「2」が食い違って読めるため）。

## IADR-0115 の分類（編集可否の確認）

| ファイル | 分類 | 根拠 |
| --- | --- | --- |
| [`scripts/check-unit-dependencies.js`](../../scripts/check-unit-dependencies.js) | **C（本リポの中身そのもの）** | IADR-0115 決定 2 の**固有デルタ種 3**「本リポにしか存在しない成果物・スクリプト」に `check-unit-dependencies.js` が名指しで挙がっている（同 IADR 62–66 行）。キット `repo-template` に対応物が無く、本リポジトリでの編集はデルタを増やさない。キットへの環流も不要 |
| `docs/specs/`（本仕様書） | **C（リポ固有）** | 雛形から書き起こした実体 |

## 受け入れ基準

- [x] `grep -n "2 プロジェクト" scripts/check-unit-dependencies.js` が **0 件**
- [x] 是正後の記述が IADR-0117 の決定内容と整合する（3 プロジェクト＝ Contracts / Infrastructure / Kernel、
      `Platform.Shared.Kernel` は未作成、改定の典拠が IADR-0117 と分かる）
- [x] `node scripts/check-unit-dependencies.js --self-test` が成功する
- [x] `node scripts/check-unit-dependencies.js`（本検査）が成功する
- [x] `node scripts/scripts.test.js`（`REQUIRE_REPO_TESTS=1` 含む）が成功する
- [x] `node scripts/check-doc-links.js` が成功する
- [x] `node scripts/check-commit-messages.js --base origin/develop` が成功する
- [x] `git diff` の変更がコメントと違反メッセージ文言のみで、判定ロジック・エクスポート・
      自己試験ケースに差分が無い

## 検証結果（実測）

| コマンド | 結果 |
| --- | --- |
| `grep -n "2 プロジェクト" scripts/check-unit-dependencies.js` | 0 件 |
| `node scripts/check-unit-dependencies.js --self-test` | exit 0（13 件 OK） |
| `node scripts/check-unit-dependencies.js` | exit 0（違反なし） |
| `node scripts/scripts.test.js` | exit 0 |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | exit 0 |
| `node scripts/check-doc-links.js` | exit 0 |
| `node scripts/check-commit-messages.js --base origin/develop` | exit 0 |

## リスクと影響

- 影響はコメントと違反時メッセージの文言のみで、検査の判定結果・終了コード・CI ゲートは変わらない。
- 96 行は実行時に出力される文字列であるため、CI ログ・アノテーションの文面が 1 語変わる。
  どのテストもこの文言を assert していないことを実測で確認済み。
- 実体プロジェクト未作成の期間は文言の「3」と実在の「2」が食い違うが、IADR-0117 決定 4 が
  意図した状態であり、コメント内に「未作成」と明記して読み手の混乱を避ける。

## フォローアップ（本 issue の範囲外）

1. `Knowledge.Bff.Endpoints.csproj` 12 行目のコメント「Shared の 2 プロジェクト」の追随（別 issue 候補）。
