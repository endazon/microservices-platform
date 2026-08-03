---
title: バックエンドカバレッジ床の IADR 起票（IADR-0118）・FR-17〜21 の着手保留（IADR-0119）と IADR-0116 のフォローアップ消化
type: spec
status: in-progress
related_ids: [NFR, IADR-0034, IADR-0116, IADR-0117, IADR-0118, IADR-0119]
author: Claude
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# 仕様書: バックエンドカバレッジ床の IADR 起票（IADR-0118）・FR-17〜21 の着手保留（IADR-0119）と IADR-0116 のフォローアップ消化

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 品質・保守性。決定の記録とゲートの明文化）
- ユースケース（UC）/ 画面（SC）: なし。ただし**言及の対象**として `FR-17`〜`FR-21`（計画側で起案段階）
- 関連 ADR: [IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md)（フロントのカバレッジゲート。書式と
  設計の下敷き）／[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md)（再実装の
  進行規約。規約 6 の具体を追記し、規約 7 の適用範囲は IADR-0119 で拡張する）／
  [IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md)（直近の IADR。書式の参照であり、
  **Accepted な IADR の部分改定を新 IADR で行う先例**でもある）／
  **[IADR-0118](../adr/IADR-0118_backend-coverage-floor.md)（本作業で起票）**／
  **[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md)（本作業で起票。FR-17〜21 の着手保留）**
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
| `ls docs/adr/` | 着手時点の最大番号は **IADR-0117**（#455 で使用済み）。**0118 が次の空き番号**であり、本作業で 0118 を採番したため **0119 がその次の空き番号**である |
| [`src/coverage-floor.json`](../../src/coverage-floor.json) | 床は `line 34` / `branch 17`。`$comment` に実測値・切り下げ・ratchet・段階ポリシーの典拠が書かれている |
| [`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js) | 外部依存ゼロ・Cobertura 直接集計・行数加重・`EXCLUDED_UNITS = {ai-stock-trading}`・レポート 0 件は fail-open（warn ＋ 内訳出力） |
| [20260803_issue-453](20260803_issue-453_regression-test-foundation.md) | 実測 `line 34.46%（18894/54826）` / `branch 17.62%（3154/17896）`・レポート 14 件。MSP の 14 テストプロジェクトが `coverlet.collector` を参照しておらず**計測されていなかった**事実 |
| [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) | ゲート一覧・検査対象ユニットの切り分け・合成点経由の混入（230〜266 行）・床の置き方 |
| `scripts/scripts.repo.test.js` | 床の `null` 化検知・全テストプロジェクトの `coverlet.collector` 参照検知の 2 本（fail-open の代償を塞ぐ退行防止テスト） |
| [`scripts/README.md`](../../scripts/README.md) | `check-permission-denials.js` 節が段階ポリシー（「成果物は正しいのに赤」を常態化させない）の設計。**失敗判定は件数の許容値（既定 4）とターン数の半分**で段階化し、`STRICT_PERMISSION_DENIALS=1` で旧挙動へ戻せる |
| [20260802_impl-handoff-kit-sync](20260802_impl-handoff-kit-sync.md) の対応表 | 段階ポリシーの当事者を実測: **planning#146**＝「成果物は正しいのに赤」（読み取り系 git の拒否が差分と無関係に毎回出る）／**planning#160**＝拒否報告が原因を隠す（複合コマンドのラベル付けが先頭セグメント固定で、許可済みの `git diff` / `git show` を「拒否された」と報告）／**planning#161**＝ラベル是正後もなお拒否が 4 件残る／**planning#162**＝「1 件でも失敗」の常態化が検査の目的を壊すため**段階ポリシーへ変更**。**planning#149 は「サブエージェント禁止の置き場所」であり段階ポリシーとは無関係** |
| [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) §フォローアップ | 「#453 完了時に、本 IADR の規約 6（受け入れゲート）へ具体的なコマンド / しきい値を追記する」が未消化 |
| 計画 [01_requirements.md](../../planning/projects/microservices-platform/02_requirements/01_requirements.md) | 注（2026-08-01・起案）: **FR-17・FR-18 は起案段階**で実現方式は ADR-0033〜0035 で確定する。**FR-19・FR-20・FR-21 は起案段階（`draft` 相当）**で確定として扱わない。**FR-19・FR-20 は前提未確定**（ADR-0036・Wiki.js 個人スコープの前提検証・ADR-0037）。注（2026-08-02）: ADR-0033・0034・0036・0037 は `Proposed`、**ADR-0035 は実測待ちで未起案**。注（2026-08-02・本書の状態の扱い）: 本書全体は `fixed` を維持し、注記で区別する |

**段階ポリシーの典拠は `scripts/README.md` の `check-permission-denials.js` 節と、その前段の失敗モード
planning#146・planning#160 ／ 段階ポリシーを導入した planning#161・planning#162 である。IADR-0115 を典拠に
しない**（同 IADR に該当記述は無い。IADR-0115 は impl-handoff-kit の**同期規約**としてのみ言及する）。
**planning#149 は典拠に含めない**——[20260802_impl-handoff-kit-sync](20260802_impl-handoff-kit-sync.md) の
対応表で planning#149 は「サブエージェント禁止の置き場所」であり、段階ポリシーとは無関係だからである。

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
6. **[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) の起票**（新規）。**FR-17〜21 の着手保留**は
   規約 7 の**適用範囲を拡張する新しい決定**であるため、IADR-0116 への追記ではなく新 IADR で決定する
   （先例 [IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md)）。IADR-0116 側の追記は
   **相互リンク中心の短い形**に留め、あわせて**規約 3 の具体 ID に `IADR-xxxx` を含む**旨を明確化する
   （決定内容の変更ではない）
7. **[`docs/adr/README.md`](../adr/README.md) の索引に IADR-0119 の行を追加**

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
| 6 | 設計原則「**成果物は正しいのに赤**を常態化させない段階ポリシー」。典拠は `scripts/README.md` の `check-permission-denials.js` 節と、前段の失敗モード planning#146・planning#160 ／ 段階ポリシーを導入した planning#161・planning#162 |

検討した選択肢としては、集計方式（自前集計 / `reportgenerator` / coverlet の `/p:Threshold=`）、重み付け
（行数加重 / 単純平均）、床の初期値の置き方（切り下げ / 実測そのまま / 切り上げ / 推測値）、レポート 0 件時
（fail-open / fail-closed）の 4 軸を表で残す。

### 2. IADR-0116 への追記（Accepted 本文は書き換えない）

既存の改訂作法（[IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md) の
`> **［2026-08-03 追記］…**` 形式）に倣い、**決定の番号付きリストの直後に追記ブロックを置く**。番号付き
リストの途中に挿入すると採番が崩れるため、リストの外に置く。

- **規約 6 の具体**: 4 ゲートの表（コマンド / しきい値）。
  `check-test-traceability.js`（写像・fail）／`check-coverage-floor.js`（床 `line 34` / `branch 17`）／
  `check-backend-libraries.js`（ADR-0030 の baseline ratchet）／フロントは vitest thresholds（IADR-0034）。
  床の値は ratchet で動くため「**値の正は `src/coverage-floor.json`**」と明記する。予告部分を実値で埋める
  ものであり、規約 6 の内容は変えない。
- **規約 7 の適用範囲と規約 3 の明確化**: 追記ブロックは**相互リンク中心の短い形**とする。
  - FR-17〜21 の着手保留とその着手条件は
    [IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) が決定する旨（＝規約 7 の適用範囲を
    そこで拡張した旨）を示し、根拠と条件の記述は IADR-0119 を正とする。
  - **規約 3 の「具体 ID」には `IADR-xxxx` を含む**（`.claude/rules/traceability.md` の起点 ID 種別と
    整合させる記述の明確化であり、決定内容の変更ではない旨を書く）。
- §フォローアップの該当項目は打ち消し線＋「**消化済み（2026-08-03・#474）**」に更新する。

### 2-2. IADR-0119 の構成（FR-17〜21 の着手保留）

IADR-0116 規約 7 は Accepted であり、その**適用範囲**（保留対象）を FR-19〜21 まで広げることは追記では
なく**新しい決定**である。先例 [IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md)
（Accepted な IADR-0056 決定 3 の部分改定を新 IADR で実施）に倣い、IADR-0119 として起票する
（書式は IADR-0117 / IADR-0118 を踏襲・`status: Accepted`）。

- **根拠**: 計画 `02_requirements/01_requirements.md` の注記の実測（上表の該当行）。FR-17〜21 が起案段階で
  あること、FR-19・FR-20 の前提 3 点が未確定であること、ADR-0033・0034・0036・0037 が `Proposed` で
  ADR-0035 が未起案であること。
- **決定**: FR-17〜21 の実装に着手しない。着手条件は前提 ADR の**確定（`Accepted` 化）**に連動する
  （FR-17・18 は ADR-0033〜0035、FR-19・20 は加えて ADR-0036・0037 と Wiki.js 個人スコープ可視性の前提検証、
  FR-21 は計画側の確定）。計画の確定を助ける作業（#456 の実測等）は保留対象外。IADR-0116 は `Accepted` の
  まま残置し、規約 7 の既存の保留（#448 / #450）と規約 1〜6 は有効のままとする。

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
- [ ] `docs/adr/IADR-0119_fr17-21-hold-until-adr-fixed.md` があり、FR-17〜21 の着手保留と着手条件
      （前提 ADR の確定への連動）が IADR-0117 / IADR-0118 の書式（`status: Accepted`）で決定されている
- [ ] `docs/adr/README.md` の索引に IADR-0119 の行がある（`IADR-0118` の直後・状態 `Accepted`）。
      索引の行数と `docs/adr/IADR-*.md` のファイル数が一致する
- [ ] 同 規約 7 への追記が**相互リンク中心の短い形**であり、保留の決定そのものは IADR-0119 にある
      （追記側に実質的な新決定を書かない）。規約 3 の具体 ID に `IADR-xxxx` を含む明確化が 1 行入っている
- [ ] 段階ポリシーの典拠が planning#146・planning#160（前段の失敗モード）／planning#161・planning#162
      （段階ポリシーの導入）として記載され、**planning#149 を典拠に含めない**。列挙形の issue 番号は
      1 件ずつ `planning#NNN` と修飾されている（裸の `#NNN` を残さない）
- [ ] `docs/specs/20260803_issue-474_backend-floor-iadr-and-0116-followup.md`（本書）がある
- [ ] `node scripts/check-doc-links.js` が破損リンク 0
- [ ] `node scripts/scripts.test.js` が全件成功（176 件）
- [ ] `node scripts/check-commit-messages.js` が成功（件名スコープ `IADR-0118` / `IADR-0119` は実在検査を通る）
- [ ] `node scripts/check-test-traceability.js` が成功

## テスト方針

本作業は文書のみのためコードのテストは追加しない。既存の機械検査で検証する。

| 受け入れ基準 | 検証手段 |
| --- | --- |
| 新規 IADR・索引・相互リンクのリンク健全性 | `node scripts/check-doc-links.js` |
| 既存検査器の非退行（文書変更が壊していない） | `node scripts/scripts.test.js`（176 件） |
| 件名スコープ `IADR-0118` / `IADR-0119` の実在 | `node scripts/check-commit-messages.js`（IADR 実在性チェック。実在検査は作業ツリーを見るため、IADR ファイルを含む同一コミットで検証する） |
| 索引とファイルの一致 | `ls docs/adr/IADR-*.md \| wc -l` と `docs/adr/README.md` の索引行数が一致すること |
| 写像規約の非退行 | `node scripts/check-test-traceability.js` |
| 床の値・方式を変更していないこと | `src/coverage-floor.json` の `backend` と `scripts/check-coverage-floor.js` に差分が無いこと（`$comment` の 1 行追加を除く） |

## 計画書との差異

- 差異: なし。本作業は計画書の解釈を変えない。むしろ計画側が起案段階と明記している FR-17〜21 を、
  実装側の決定（[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md)。IADR-0116 規約 7 の適用範囲を
  拡張する）へ反映して**齟齬を減らす**ものである。

## 未決事項

1. **合成点経由の混入の確定値**（[#468](https://github.com/endazon/microservices-platform/issues/468)）。
   230〜266 行の範囲のまま IADR-0118 に記録する。除去後の推定はいずれも床 34 を上回るため、床の値の
   見直しは #468 の結果を見てから行う。
2. **床の引き上げ時期**。各ドメイン issue（#438〜#451）がテストを追加した時点で ratchet する。引き上げ時は
   IADR-0116 規約 6 の追記表も追随させる（値の正は `src/coverage-floor.json`）。
