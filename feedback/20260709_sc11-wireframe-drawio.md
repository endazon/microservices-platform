---
title: SC-11 構成ビューアのワイヤーフレーム（sc-11.drawio）を計画リポジトリへ追加
type: plan-feedback
status: rejected
category: UC/画面の差異
related_ids: [FR-15, SC-11, ADR-0018, IADR-0036]
source_repo: microservices-platform
source_ref: "docs/screens/SC-11_configuration-viewer.md（未決事項5）/ IADR-0036 /（履歴データ源は PR #189・#139 で導入予定の IADR-0046）"
author: claude
created: 2026-07-09
updated: 2026-08-05
---

# フィードバック: SC-11 構成ビューアのワイヤーフレーム（sc-11.drawio）を計画リポジトリへ追加

## 取り下げ（2026-08-05 / #504）— **計画は draw.io を作らない方針である**

**本記録は取り下げる（`status: closed`）。** #504 の着手時に計画リポジトリ（pin `d980a01`）を実測した結果、
**計画は HTML モックアップを正とし draw.io を作成しない**方針であることが確かめられた。

| 実測 | 内容 |
| --- | --- |
| `05_screens/01_screens.md` §HTMLモックアップ | 全 21 画面について **hi-fi と wireframe の HTML** を表で挙げている。SC-11 も [wireframe/sc-11.html](../planning/projects/microservices-platform/05_screens/mockups/wireframe/sc-11.html) が揃っている |
| 計画リポジトリ全体 | **`.drawio` ファイルは 1 件も存在しない**（`05_screens/wireframes/` というディレクトリ自体が無い） |

すなわち本記録が前提にしていた「他画面はワイヤーフレームを持つが SC-11 だけ未作成」という認識が誤りであり、
**SC-11 のワイヤーフレームは HTML として既に存在する**。計画側へ渡す作業は無い。

**実装側の追随**: `docs/screens/SC-11_configuration-viewer.md` の未決事項 5 は #504 で
「解決して畳んだ未決事項」へ移し、**HTML の wireframe / hi-fi を実装の正**として本文から参照した。

## 種別

UC/画面の差異（画面成果物の不足: ワイヤーフレーム未作成）。

## 起点となる計画書

- 機能要求（FR）: FR-15（構成の可視化・ドリフト検出）
- 画面（SC）: SC-11（構成ビューア）
- 関連 ADR: ADR-0018（コンポーザブル）
- 計画書リンク: `05_screens/01_screens.md (SC-11)` / `05_screens/wireframes/`（sc-11.drawio が不在）

## 現状（計画書の記述 / As-Is）

- 計画リポジトリに **`05_screens/wireframes/sc-11.drawio` が存在しない**。他画面はワイヤーフレームを持つが
  SC-11 は未作成。実装仕様書（`docs/screens/SC-11_configuration-viewer.md`）の未決事項 5 で
  「計画リポジトリ側の作業」と整理済み。

## 問題点 / あるべき姿（To-Be）

- SC-11 の画面成果物（ワイヤーフレーム）が計画側に無く、実装仕様書とのトレーサビリティ（画面図→実装）が
  片欠けになっている。計画側に sc-11.drawio を追加し、SC-11 の画面記述からリンクすべき。
- なお SC-11 の可視化方式は**グラフ描画ライブラリを導入せず CSS 縦チェーン＋表**で確定済み
  （[[IADR-0036]]）。ワイヤーフレームはこの方式（実効構成／ドリフト／履歴の 3 折りたたみセクション）に整合させる。
- 履歴のデータ源決定（GitOps 層）は **PR #189・#139 で導入予定の IADR-0046**（本 PR のベース develop には未マージ）で、
  マージ後に確定する。ワイヤーフレームの履歴セクションはこの決定に整合させる。

## 実装で判明した経緯

- SC-11 画面実装（#137 グラフ／#138 ドリフト／#139 履歴・PR #189）を通じて、画面レイアウトが確定した
  （実効構成→ドリフト→バージョン履歴の順、各セクションは `<details>` 折りたたみ、履歴は表形式）。
- 履歴のデータ源も IADR-0046（#139・PR #189、develop へは未マージ）で GitOps 層に確定させる方針で、画面の
  情報要素（コミット ID・適用日時・適用者・ドリフト有無）が固まった。ワイヤーフレームはこれを反映できる状態。

## 提案（計画への反映案）

- 反映先候補: **UC・画面更新**（`05_screens/wireframes/sc-11.drawio` の新規作成 ＋ SC-11 記述からのリンク）。
- 提案内容（実装で確定したレイアウトを図案化）:
  1. ヘッダ: 構成バージョン（コミット ID 短縮・適用日時・適用者）。
  2. セクション(1) 実効構成: パイプライン段の縦チェーン（consumer → outputs・無効段グレーアウト・ドリフト段は警告色）、
     イベント接続・ポート選択・コネクタの表。
  3. セクション(2) 宣言との差分（ドリフト）: 種別・深刻度・対象・説明の表＋バッジ「ドリフト N 件 / OK」。
  4. セクション(3) 構成バージョン履歴: コミット ID・適用日時・適用者・ドリフト有無の表（新しい順）。
  5. アクセス: 管理者・運用者限定（存在秘匿）。参照専用（構成変更は GitOps）。

## 影響範囲

- 計画リポジトリへの画面図追加と SC-11 記述リンクのみ。実装（#137/#138/#139）は完了済みで整合。
- 反映後、実装側 `docs/screens/SC-11_configuration-viewer.md` 未決事項 5（ワイヤーフレーム）を「解決済み」へ更新する
  （本リポジトリ側の追随作業）。なお未決事項 3（履歴データ源）は #139・PR #189 マージ時に IADR-0046 で解決済みへ更新する。

## ［2026-08-05 追記 / #497］計画側の実態へ status を同期した

**判定: rejected（別解で解消）。理由: 計画は HTML モックアップを正とし draw.io ワイヤーフレームを作成しない方針であり、SC-11 のワイヤーフレームは既に HTML として存在するため、計画側へ渡す作業が成立しない**（上の「取り下げ」節を参照）。

> **本記録は #504（PR #511）が `status: closed` として先行して取り下げていた。** `closed` は計画リポジトリが定める語彙（`open` / `triaged` / `accepted` / `rejected`）に無い一点物であったため `rejected` へ揃えた。**取り下げの判断そのものは #504 のまま変更していない。**

確認は planning submodule pin `d980a01` に対して行った（**行番号は pin が動くとずれるため内容で特定する**）。

| 確認先（計画リポジトリ） | 確認した記述 |
| --- | --- |
| [05_screens/01_screens.md](../planning/projects/microservices-platform/05_screens/01_screens.md) `:39` | 「ワイヤーフレームは HTML モックアップ〔`mockups/wireframe/`〕を正とし、**draw.io ワイヤーフレームは作成しない**」——**別解が明文で確定している** |
| [05_screens/mockups/wireframe/sc-11.html](../planning/projects/microservices-platform/05_screens/mockups/wireframe/sc-11.html) | SC-11 のワイヤーフレームが**実在する**（計画リポジトリに `.drawio` は 1 件も無い） |
| [draft/feedback/20260709_sc11-wireframe-drawio.md](../planning/draft/feedback/20260709_sc11-wireframe-drawio.md) | **`status: open` のまま**。原典（計画リポジトリ）は未追随であり、その追随は計画側の作業である（本リポジトリからは触れない） |

作業仕様書: [docs/specs/20260805_issue-497_feedback-status-sync.md](../docs/specs/20260805_issue-497_feedback-status-sync.md)（#497）
