---
title: 構成バージョン履歴の正データ源（GitOps 層）と保持方針を計画の運用設計へ環流
type: plan-feedback
status: accepted
category: 新たな制約(ADR要)
related_ids: [FR-15, SC-11, ADR-0018]
source_repo: microservices-platform
source_ref: "PR #189 / #139 で導入予定の IADR-0046 / docs/screens/SC-11_configuration-viewer.md（未決事項3）"
author: claude
created: 2026-07-09
updated: 2026-08-08
---

# フィードバック: 構成バージョン履歴の正データ源（GitOps 層）と保持方針を計画の運用設計へ環流

## 種別

新たな制約（ADR 相当の運用設計判断）。

## 起点となる計画書

- 機能要求（FR）: FR-15（構成の可視化・ドリフト検出・構成バージョン）
- 画面（SC）: SC-11（構成ビューア §(3) バージョン履歴）
- 関連 ADR: ADR-0018（コンポーザブル・GitOps）
- 計画書リンク: `06_technical/05_observability-ops.md`（監査ログ・適用履歴の保持方針）／`05_screens/01_screens.md (SC-11)`

## 現状（計画書の記述 / As-Is）

- 計画リポジトリの運用設計（`06_technical/05_observability-ops.md`）に、**構成の適用履歴（構成バージョン履歴）の
  正データ源・保持方針**が明記されていない。ADR-0018 のフォローアップとしても未反映。
- 実装側は SC-11 の履歴表示（#139）実装にあたり、履歴の正データ源を決める必要があった。

## 問題点 / あるべき姿（To-Be）

- 構成バージョン履歴の「正データ源」と「保持範囲」を計画の運用設計に明記すべき。実装側は
  **GitOps 層（Git コミット履歴 / ArgoCD リビジョン履歴）を正**とし、プラットフォームのサービスに履歴ストアを
  新設しない方針を採った（実装 ADR：IADR-0046・PR #189・#139）。計画側の運用設計もこの前提と整合させたい。

## 実装で判明した経緯

- SC-11 履歴表示（#139）実装時、履歴の正データ源が未決（SC-11 仕様書 未決事項 3）だった。
- 実装判断（IADR-0046）: 正データ源＝GitOps 層。API（`/bff/admin/config/history`）は現在バージョンと同じ
  注入経路（GitOps→構成）で供給される履歴スライスを**永続化せず** surfacing する。保持範囲は GitOps 側が決定。
  未注入（dev/compose）は現在バージョン単一へ縮退。理由: SC-11 は参照専用で構成変更は GitOps のみ（Git 自体が
  不変の適用履歴台帳）、サービス DB への履歴複製は第二の真実を生み依存最小方針（IADR-0033/0036）と不整合。

## 提案（計画への反映案）

- 反映先候補: **新 ADR もしくは運用設計更新**（`06_technical/05_observability-ops.md` に「構成適用履歴の正データ源＝
  GitOps 層／API は非永続 surfacing／保持は GitOps 側」を明記）。
- 提案内容:
  1. 構成バージョン履歴の正データ源を **GitOps 層**（Git コミット履歴 / ArgoCD リビジョン）と定義。
  2. 保持範囲は GitOps 側の設定（Git 履歴は実質無制限、ArgoCD 保持リビジョン数）に委ね、プラットフォームの
     サービスには履歴ストアを持たないことを制約として明記。
  3. 監査ログ（構成情報 API の取得監査）と適用履歴（構成バージョン履歴）は別系統である旨を整理。

## 影響範囲

- 計画の運用設計（observability-ops）追補と、必要なら計画 ADR の追加。実装（#139）は**完了済み・develop 未マージ**
  （PR #189）で整合する見込み。
- 実装との整合: IADR-0046（PR #189・#139。develop 未マージ）が対応。GitOps 側の `Config:History` 注入配線は残作業（#123 で追跡）。
