---
title: IADR-0046 構成バージョン履歴の正データ源は GitOps 層とし、API は注入スライスを surfacing する
type: impl-adr
status: Accepted
related_ids:
  - FR-15
  - SC-11
  - ADR-0018
  - IADR-0029
  - IADR-0033
  - IADR-0036
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
  - planning:projects/microservices-platform/06_technical/10_composability-design.md (§設計要素6)
---

# IADR-0046: 構成バージョン履歴の正データ源は GitOps 層とし、API は注入スライスを surfacing する

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-15（構成情報 API・イントロスペクション・ドリフト検出）／SC-11（構成ビューア）／ADR-0018（コンポーザブル）
- 関連 ADR: [IADR-0029](./IADR-0029_config-info-api-placement-and-drift-granularity.md)（構成情報 API の配置・ドリフト粒度）／[IADR-0033](./IADR-0033_frontend-spa-foundation.md)（フロント基盤・依存最小）／[IADR-0036](./IADR-0036_sc11-config-viewer-visualization.md)（SC-11 可視化方式）
- 関連仕様書: `docs/screens/SC-11_configuration-viewer.md`（未決事項 3）／`docs/tests/SC-11_configuration-viewer.md`
- Issue: #139（親 #122・調整 #123）

## コンテキストと課題

SC-11（構成ビューア）は実効構成・ドリフトに加え、**構成バージョン適用履歴**（コミット ID・適用日時・
適用者・その時点のドリフト有無）を新しい順で表示する（SC-11 §(3)）。#112（PR #116）で「現在の」実効構成・
ドリフト・現在バージョン（`ConfigVersionOptions`）は提供済みだが、**適用履歴の正データ源が未決**だった
（SC-11 未決事項 3）。選択肢は次の 2 系統。

1. **GitOps/ArgoCD の適用履歴を正とする**（Git のコミット履歴 / ArgoCD の Application リビジョン履歴）。
2. **API 側で適用履歴を保持する**（プラットフォームのサービスに履歴ストアを新設）。

## 決定

**構成バージョン履歴の正データ源は GitOps 層（1）とし、プラットフォームのサービスに履歴ストアを新設しない。**
API（`/bff/admin/config/history`）は、現在バージョンと**同じ注入経路**（GitOps→構成）で供給される
`ConfigVersionOptions.History`（新しい順の適用履歴スライス）を、永続化せずそのまま surfacing する。

- **保持範囲**は GitOps 側が決定する（Git のコミット履歴は実質無制限、ArgoCD は保持リビジョン数）。API は
  注入されたスライスを返すのみで、保持ポリシーを二重に持たない。
- **縮退**: 履歴が未注入の環境（dev / compose）では、現在バージョンの単一エントリへ縮退する。現在バージョンも
  空なら空一覧を返す（dev で `gitCommit` が空になる既知挙動と一貫）。
- **各エントリ**は `gitCommit` / `appliedAt` / `appliedBy` に加え、その時点のドリフト有無 `hadDrift`（`bool?`）を
  持つ。注入時に判明していれば設定し、不明なら `null`（画面は「—」表示）。縮退で合成する現在エントリは
  `hadDrift = null`（現在の実効ドリフトは `/drift` で別途取得できるため、履歴側では遡及的に断定しない）。

## 根拠 / 代替案

- **API 側の履歴ストア新設（2）を採らない**理由:
  - SC-11 は**参照専用**で、構成変更は Git 経由（GitOps）に限る（FR-15・SC-11 入力方針）。**Git 自体が
    不変の適用履歴台帳**であり、履歴の第一義的な出所は既に存在する。
  - サービス DB に履歴を複製すると **Git と乖離し得る第二の真実**を生み、永続化・保持・整合の複雑性を
    抱える。[IADR-0033](./IADR-0033_frontend-spa-foundation.md)（依存最小）・[IADR-0036](./IADR-0036_sc11-config-viewer-visualization.md)（可視化も依存を増やさない）の方針と不整合。
  - 現在バージョンが既に GitOps 注入（[IADR-0029](./IADR-0029_config-info-api-placement-and-drift-granularity.md)・`ConfigVersionOptions`）であるため、履歴はその
    注入の**時系列スライス**として自然に表現でき、新規機構を要しない。
- **GitOps 注入の配線自体**（実際に Git ログ／ArgoCD リビジョンから `Config:History` を供給する Helm/
  パイプライン設定）は #123（FR-15 残スコープ・GitOps バージョン注入）の担当。本 ADR は API 契約・縮退・
  表示の確定に閉じ、注入配線は #123 に委ねる（本 PR で contract と dev 縮退・テストは完結する）。
- **`hadDrift` を履歴で遡及計算しない**: 過去バージョンのドリフトは当時の宣言・実効に依存し、現在の
  collector からは再現できない。注入側（当時の検出結果）が持つべき情報のため、`bool?` で受けて不明は
  `null` とする（起こり得ない遡及計算の防御的実装を避ける）。

## 影響

- 契約追加: `ConfigVersionEntryDto`（Contracts）・`ConfigVersionOptions.History`（Infrastructure）・
  `IConfigInspectionService.GetVersionHistoryAsync`・`GET /bff/admin/config/history`（ConfigViewer・404 秘匿・
  監査 `config.history.read`）。
- 画面: SC-11 に「構成バージョン履歴」セクション（新しい順の表・ドリフト有無列・縮退/空表示）。
- SC-11 未決事項 3 を解決（本 ADR が決定先）。
- #123 は本 ADR の注入契約（`Config:History`）に従って GitOps 配線を行う。
