---
title: フロントエンド SC-01〜11 全画面の実装完了 — 05_screens／INDEX の「未着手」注記と draft 状態の更新提案
type: plan-feedback
status: accepted
category: UC/画面の差異
related_ids: [SC-01, SC-02, SC-03, SC-04, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-11, FR-15, ADR-0018, IADR-0033]
source_repo: microservices-platform
source_ref: "docs/screens/SC-01〜SC-11（全11画面仕様書）/ frontend/src/features/sc01〜sc11 / IADR-0033（SPA基盤）/ 2026-07-09 全体レビュー"
author: claude
created: 2026-07-09
---

> **［2026-08-04］反映済み。** 計画側が planning#189 / planning#191 のトリアージで本記録を受理し、
> [05_screens/01_screens.md](../planning/projects/microservices-platform/05_screens/01_screens.md) の
> §変更履歴 が本記録を名指しして「SC-01〜11 の実装状況」に関する注記を是正した
> （planning `d980a01` / planning PR #194。同 PR のレビュー指摘により、環流記録を `accepted` としながら
> 本文の注記が審議中のままだった食い違いも併せて解消された）。**実装側に残作業は無い。**
# フィードバック: フロントエンド SC-01〜11 全画面の実装完了 — 計画書の「未着手」注記と draft 状態の更新提案

## 種別

UC/画面の差異（計画書ステータス・注記が実装実態へ未追従）

## 起点となる計画書

- 機能要求（FR）: FR-15（SC-11 の根拠）ほか画面に対応する各 FR
- ユースケース（UC）: UC-01〜07
- 画面（SC）: SC-01〜SC-11
- 関連 ADR: ADR-0018（SC-11）
- 計画書リンク: `projects/microservices-platform/05_screens/01_screens.md`、`projects/microservices-platform/INDEX.md`

## 現状（計画書の記述 / As-Is）

- `05_screens/01_screens.md` は冒頭注記に「**フロントエンド（SC-01〜10）は未着手**であり、バックエンド確定後の後続フェーズで実装する」「本書は据え置き（draft）とする」と記載（2026-07-06/07 の注記）。
- `INDEX.md` も「フロントエンド SC-01〜10 は未着手のため `05_screens` は draft 据え置き」と記載。
- `06_technical/10_composability-design.md` は draft のまま（ADR-0018 は Accepted 済み）。

## 問題点 / あるべき姿（To-Be）

実装リポジトリでは 2026-07-08〜09 に SC-01〜SC-11 の**全 11 画面が実装・マージ済み**である（React 18 SPA。画面仕様書 `docs/screens/SC-01〜SC-11`、feature 実装 `frontend/src/features/sc01〜sc11`、各画面のテスト・カバレッジゲート付き。SPA 基盤は IADR-0033）。計画書の「未着手」注記が実態と逆転しており、計画書だけを読む関係者が誤認する。

## 実装で判明した経緯

2026-07-09 の実装リポジトリ全体レビュー（計画との突き合わせ）で、画面実装の完了と計画書注記の未更新を確認した。

## 提案（計画への反映案）

- 反映先候補: UC・画面更新（05_screens）・INDEX 更新
- 提案内容:
  1. `05_screens/01_screens.md` の「未着手」注記を「SC-01〜11 実装済み（実装リポジトリ 2026-07-09 時点）」へ更新し、状態を draft → fixed（または review）へ遷移させる。
  2. `INDEX.md` の該当記述（「フロントエンド未着手のため draft 据え置き」）を更新する。
  3. ワイヤーフレーム（`05_screens/wireframes/sc-01/05/09.drawio`「別途作成」）は未作成のままである。実装完了に伴い（a）実装スクリーンショット参照へ置き換える、（b）作成予定を取り下げる、のいずれかを判断する（sc-11 は別フィードバック 20260709_sc11-wireframe-drawio.md 参照）。
  4. `06_technical/10_composability-design.md`（draft）も FR-14/15 実装完了を受けて状態確定を検討する。

## 影響範囲

- 計画書の状態管理のみ。要求・設計内容の変更はない。
- 画面詳細（SC-01/05/09/11 の入力・バリデーション）と実装の差分は、実装リポジトリの画面仕様書（`docs/screens/`）に詳細化済みであり、必要ならトリアージ時に突き合わせる（機密区分必須のサーバー側検証は実装リポ issue #199 で追跡）。
