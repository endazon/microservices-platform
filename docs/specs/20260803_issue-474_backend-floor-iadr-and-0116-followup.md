---
title: バックエンドカバレッジ床の IADR 起票（IADR-0118）と IADR-0116 のフォローアップ消化
type: spec
status: in-progress
related_ids: [NFR, IADR-0034, IADR-0116, IADR-0118]
author: Claude
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# 仕様書: バックエンドカバレッジ床の IADR 起票（IADR-0118）と IADR-0116 のフォローアップ消化

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 品質・保守性。決定の記録とゲートの明文化）
- ユースケース（UC）/ 画面（SC）: なし。ただし**言及の対象**として `FR-17`〜`FR-21`（計画側で起案段階）
- 関連 ADR: [IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md)（フロントのカバレッジゲート。書式と
  設計の下敷き）／[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md)（再実装の
  進行規約。規約 6・7 を追記）／[IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md)（直近の
  IADR。書式の参照）／**[IADR-0118](../adr/IADR-0118_backend-coverage-floor.md)（本作業で起票）**
- 計画書リンク:
  [02_requirements/01_requirements.md](../../planning/projects/microservices-platform/02_requirements/01_requirements.md)
  （`fixed`。ただし FR-17〜21 は注記により起案段階として区別される）
- 先行する作業仕様書:
  [20260803_issue-453](20260803_issue-453_regression-test-foundation.md)（床の実装と実測値の一次情報）
- 本リポジトリの起点: #474

## 目的・背景

#453（PR #464・マージ済み）はバックエンドのカバレッジ床を導入したが、**その決定は IADR に残っていない**。
記録は作業仕様書と [`src/coverage-floor.json`](../../src/coverage-floor.json) の `$comment` にしかない。
フロントの同等ゲートが [IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md) を持つのに対し非対称であり、
後続の各ドメイン issue（#438〜#451）が「なぜこの値・この方式なのか」を追えない。`CLAUDE.md` は
「重要な実装判断は実装 ADR に必ず残す」と定めている。

あわせて [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) の §フォローアップに
**「#453 完了時に、本 IADR の規約 6（受け入れゲート）へ具体的なコマンド / しきい値を追記する」**という
未消化項目が残っている。#453 は完了済みであるため、本作業で消化する。

さらに、計画側が **FR-17〜21 を起案段階（`draft` 相当）**としていることが、進行規約に反映されていない。
規約 7 は ADR-0035 未起案を理由に #448 / #450 の該当スコープを保留しているが、要求そのものが未確定である
点は書かれていない。着手判断を誤ると、確定時に手戻りが出るか、確定した計画に反する実装が develop に残る。

### 一次情報として確認した事実

| 確認先 | 内容 |
| --- | --- |
| `ls docs/adr/` | 既存の最大番号は **IADR-0117**（#455 で使用済み）。**0118 が次の空き番号**である |
| [`src/coverage-floor.json`](../../src/coverage-floor.json) | 床は `line 34` / `branch 17`。`$comment` に実測値・切り下げ・ratchet・段階ポリシーの典拠が書かれている |
| [`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js) | 外部依存ゼロ・Cobertura 直接集計・行数加重・`EXCLUDED_UNITS = {ai-stock-trading}`・レポート 0 件は fail-open（warn ＋ 内訳出力） |
| [20260803_issue-453](20260803_issue-453_regression-test-foundation.md) | 実測 `line 34.46%（18894/54826）` / `branch 17.62%（3154/17896）`・レポート 14 件。MSP の 14 テストプロジェクトが `coverlet.collector` を参照しておらず**計測されていなかった**事実 |
| [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) | ゲート一覧・検査対象ユニットの切り分け・合成点経由の混入（230〜266 行）・床の置き方 |
| `scripts/scripts.repo.test.js` | 床の `null` 化検知・全テストプロジェクトの `coverlet.collector` 参照検知の 2 本（fail-open の代償を塞ぐ退行防止テスト） |
| [`scripts/README.md`](../../scripts/README.md) | `check-permission-denials.js` 節が段階ポリシー（「成果物は正しいのに赤」を常態化させない）の設計。典拠は planning#146 / #149 / #160 |
| [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) §フォローアップ | 「#453 完了時に、本 IADR の規約 6（受け入れゲート）へ具体的なコマンド / しきい値を追記する」が未消化 |
| 計画 [01_requirements.md](../../planning/projects/microservices-platform/02_requirements/01_requirements.md) | 注（2026-08-01・起案）: **FR-17・FR-18 は起案段階**で実現方式は ADR-0033〜0035 で確定する。**FR-19・FR-20・FR-21 は起案段階（`draft` 相当）**で確定として扱わない。**FR-19・FR-20 は前提未確定**（ADR-0036・Wiki.js 個人スコープの前提検証・ADR-0037）。注（2026-08-02）: ADR-0033・0034・0036・0037 は `Proposed`、**ADR-0035 は実測待ちで未起案**。注（2026-08-02・本書の状態の扱い）: 本書全体は `fixed` を維持し、注記で区別する |

**段階ポリシーの典拠は `scripts/README.md` の `check-permission-denials.js` 節と planning#146 / #149 / #160
である。IADR-0115 を典拠にしない**（同 IADR に該当記述は無い。IADR-0115 は impl-handoff-kit の**同期規約**
としてのみ言及する）。

## 対象範囲

本作業は**文書のみ**である。コード・CI・床の値は変更しない（#453 の決定を事後に記録するものであり、
決定内容を変える作業ではない）。

1. **[IADR-0118](../adr/IADR-0118_backend-coverage-floor.md) の起票**（新規）
2. **[`docs/adr/README.md`](../adr/README.md) の索引に IADR-0118 の行を追加**
3. **[IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md) へ相互リンクを追記**（日付付き・本文は書き換えない）
4. **[`src/coverage-floor.json`](../../src/coverage-floor.json) の `$comment` と
   [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) の床関連節に IADR-0118 への参照を追加**
   （内容は重複させず、詳細は IADR を正とする一文にとどめる）
5. **[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 6 へ具体を追記**し、
   §フォローアップの該当項目を消化済みに更新
6. **同 規約 7 へ FR-17〜21 の起案段階の注記を追記**

### 対象外

- 床の値の引き上げ・引き下げ（#453 の決定を維持する。引き上げはテストを増やす issue が行う）
- 合成点経由の混入の除去（[#468](https://github.com/endazon/microservices-platform/issues/468)）
- `ci.yml` / `scripts/` の変更

## 設計

### 1. IADR-0118 の構成

[IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md) と
[IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md) の書式を踏襲する
（frontmatter に `type: impl-adr` / `status: Accepted` / `related_ids` / `plan_refs`、本文は
「起点・関連 → コンテキストと課題 → 検討した選択肢 → 決定 → 理由 → 結果 → 関連」）。

記録する決定は 6 点である（**すべて #453 で実装済みの事実**であり、本 IADR で新たに変える事項は無い）。

| # | 決定 |
| --- | --- |
| 1 | 単一情報源は `src/coverage-floor.json`、検査器は `check-coverage-floor.js`（外部依存ゼロ・Cobertura 直接集計・**行数加重**） |
| 2 | 床は**実測からの整数切り下げ**（初期値 `line 34` / `branch 17`。実測 34.46 / 17.62・レポート 14 件。切り上げは初回 fail するため不採用。推測値は置かない） |
| 3 | 運用は **ratchet**（テストを増やしたら床を引き上げる。引き下げは退行） |
| 4 | **AST（`ai-stock-trading`）除外**と、その既知の限界（合成点経由の混入 230〜266 行・除去は #468。除去後の推定はいずれも床 34 を上回るため床は有効） |
| 5 | レポート 0 件は **fail-open**（warn ＋ 内訳出力）とし、その代償（床の無音失効）を `scripts.repo.test.js` の退行防止テスト 2 本で塞ぐ |
| 6 | 設計原則「**成果物は正しいのに赤**を常態化させない段階ポリシー」。典拠は `scripts/README.md` の `check-permission-denials.js` 節 / planning#146・#149・#160 |

検討した選択肢としては、集計方式（自前集計 / `reportgenerator` / coverlet の `/p:Threshold=`）、重み付け
（行数加重 / 単純平均）、床の初期値の置き方（切り下げ / 実測そのまま / 切り上げ / 推測値）、レポート 0 件時
（fail-open / fail-closed）の 4 軸を表で残す。

### 2. IADR-0116 への追記（Accepted 本文は書き換えない）

既存の改訂作法（[IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md) の
`> **［2026-08-03 追記］…**` 形式）に倣い、**決定の番号付きリストの直後に追記ブロックを置く**。番号付き
リストの途中に挿入すると採番が崩れるため、リストの外に置き「規約 6 の具体」「規約 7 の補足」と明示する。

- **規約 6 の具体**: 4 ゲートの表（コマンド / しきい値）。
  `check-test-traceability.js`（写像・fail）／`check-coverage-floor.js`（床 `line 34` / `branch 17`）／
  `check-backend-libraries.js`（ADR-0030 の baseline ratchet）／フロントは vitest thresholds（IADR-0034）。
  床の値は ratchet で動くため「**値の正は `src/coverage-floor.json`**」と明記する。
- **規約 7 の補足**: FR-17〜21 が計画側で起案段階であること、FR-19・FR-20 の前提 3 点が未確定であること、
  ADR-0035 が未起案であること（規約 7 の既存言及と整合させる）、したがって着手は前提 ADR の確定を待つこと。
- §フォローアップの該当項目は打ち消し線＋「**消化済み（2026-08-03・#474）**」に更新する。

### 3. 重複の回避

床の値・実測値・限界の詳細は**IADR-0118 を正**とする。`src/coverage-floor.json` の `$comment` と
`TEST_STRATEGY.md` へは**参照の一文のみ**を足し、既存の記述を削らない（`$comment` は機械が読む JSON の
傍らにある運用メモであり、IADR を読まずに床を触る人が最低限の文脈を得られる状態を保つ）。

## 受け入れ基準

- [ ] `docs/adr/IADR-0118_backend-coverage-floor.md` があり、上表 6 点の決定と検討した選択肢が
      IADR-0034 / IADR-0117 の書式（frontmatter・節構成・`status: Accepted`）で記録されている
- [ ] `docs/adr/README.md` の索引に IADR-0118 の行がある（`IADR-0117` の直後・状態 `Accepted`）
- [ ] `docs/adr/IADR-0034_frontend-coverage-gate.md` に日付付きの相互リンク追記があり、**本文（決定・理由）は
      変更されていない**
- [ ] `src/coverage-floor.json` の `$comment` と `docs/tests/TEST_STRATEGY.md` の床関連節に IADR-0118 への
      参照がある（内容の重複なし）
- [ ] `docs/adr/IADR-0116_reimplementation-branching-and-pr-policy.md` の規約 6 に 4 ゲートの具体
      （コマンド / しきい値）が追記形式で入り、§フォローアップの該当項目が消化済みに更新されている
- [ ] 同 規約 7 に FR-17〜21 の起案段階の注記が入り、ADR-0035 未起案への既存言及と整合している
- [ ] `docs/specs/20260803_issue-474_backend-floor-iadr-and-0116-followup.md`（本書）がある
- [ ] `node scripts/check-doc-links.js` が破損リンク 0
- [ ] `node scripts/scripts.test.js` が全件成功（176 件）
- [ ] `node scripts/check-commit-messages.js` が成功（件名スコープ `IADR-0118` は実在検査を通る）
- [ ] `node scripts/check-test-traceability.js` が成功

## テスト方針

本作業は文書のみのためコードのテストは追加しない。既存の機械検査で検証する。

| 受け入れ基準 | 検証手段 |
| --- | --- |
| 新規 IADR・索引・相互リンクのリンク健全性 | `node scripts/check-doc-links.js` |
| 既存検査器の非退行（文書変更が壊していない） | `node scripts/scripts.test.js`（176 件） |
| 件名スコープ `IADR-0118` の実在 | `node scripts/check-commit-messages.js`（IADR 実在性チェック） |
| 写像規約の非退行 | `node scripts/check-test-traceability.js` |
| 床の値・方式を変更していないこと | `src/coverage-floor.json` の `backend` と `scripts/check-coverage-floor.js` に差分が無いこと（`$comment` の 1 行追加を除く） |

## 計画書との差異

- 差異: なし。本作業は計画書の解釈を変えない。むしろ計画側が起案段階と明記している FR-17〜21 を、
  実装側の進行規約（IADR-0116 規約 7）へ反映して**齟齬を減らす**ものである。

## 未決事項

1. **合成点経由の混入の確定値**（[#468](https://github.com/endazon/microservices-platform/issues/468)）。
   230〜266 行の範囲のまま IADR-0118 に記録する。除去後の推定はいずれも床 34 を上回るため、床の値の
   見直しは #468 の結果を見てから行う。
2. **床の引き上げ時期**。各ドメイン issue（#438〜#451）がテストを追加した時点で ratchet する。引き上げ時は
   IADR-0116 規約 6 の追記表も追随させる（値の正は `src/coverage-floor.json`）。
