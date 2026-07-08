---
title: 作業仕様書 — スカッシュマージ件名（PR タイトル）の規約チェック再発防止
type: spec
status: in-progress
related_ids:
  - NFR
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../.claude/rules/traceability.md"
related_specs:
  - ../adr/IADR-0015_changelog-generation-time-correction.md
---

# 作業仕様書: PR タイトル（スカッシュ後件名）の規約チェック

Issue: #125（関連: #118 監査論点 4 / #60 コミット件名の機械チェック / IADR-0015）。

## 起点となる計画書（トレーサビリティ）

- 非機能: NFR（CI 品質ゲート・トレーサビリティ再発防止）
- 規約: `.claude/rules/traceability.md`「コミットメッセージの機械チェック（CI・再発防止）」
- 関連決定: IADR-0015（CHANGELOG 生成時補正の枠組み。事後補正の枠組みであり事前防止ではない）

## 目的・背景

コミット `3d8852f`（PR #95）のような**スカッシュマージ時の規約外件名**が CI をすり抜けて
CHANGELOG に生載りする問題を再発防止する。

現行 `scripts/check-commit-messages.js` は PR 上のコミット（`base..HEAD`）を検査するが、
**スカッシュマージで生成されるマージ後件名は PR タイトルに由来**し、この範囲に含まれないため未検査。
規約外の PR タイトルが develop 履歴に入り得る。

## 方式選定（要判断 → 決定）

Issue のスコープは (1) PR タイトルを CI で検査 / (2) push 後にマージ後件名を検査・通知 の選定。

**採用: (1) PR タイトルの CI 検査。** 理由:

- スカッシュ既定の件名は「PR タイトル + ` (#123)`」であり、PR タイトルを検査すれば**マージ前に**
  不正件名をブロックできる（事前防止 > 事後検出）。
- 既存 `validateSubject`（規約の単一情報源）をそのまま再利用でき、二重定義を生まない。
- (2) は事後検出に留まり、既に develop へ入った後の是正は IADR-0015 の changelog-overrides remap
  （生成時補正）で対応するという既存枠組みと役割分担が明確。

## 対象範囲

- 対象:
  1. `scripts/check-commit-messages.js` に**単一件名検査モード**を追加（`--title <s>` 引数 / `PR_TITLE`
     環境変数）。既存の `validateSubject` / `isSkippable` を再利用し、bot・Revert・`[skip ci]` は除外。
  2. 新ワークフロー `.github/workflows/pr-title.yml`。`pull_request` の `opened/edited/reopened/synchronize`
     で起動し、PR タイトルを検査。bot 作成 PR（`pull_request.user.type == 'Bot'`）は除外。
  3. `scripts/scripts.test.js` に単一件名モードの単体テストを追加。
  4. `.claude/rules/traceability.md` に PR タイトル検査と、changelog-overrides remap との使い分けを追記。
- 非対象: 既存 develop 履歴の書き換え（force push 禁止・IADR-0015 の原則）。

## 受け入れ基準

- [ ] 規約外の PR タイトルで PR を開く/編集すると CI（pr-title）が失敗する。
- [ ] スカッシュ既定末尾 ` (#123)`・bot 由来 PR・Revert・`[skip ci]` の除外規定と整合する。
- [ ] 再発時運用（changelog-overrides remap との使い分け）が `.claude/rules/traceability.md` に文書化される。
- [ ] `node scripts/scripts.test.js` が緑（追加テスト含む）。

## テスト

- 単一件名モード: 正常件名（`feat(FR-08): ...`）合格、末尾 `(#123)` 許容、規約外（`update stuff`）違反、
  Revert/`[skip ci]` はスキップ扱い。
