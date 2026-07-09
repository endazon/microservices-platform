---
title: SC-11 構成バージョン履歴表示とデータ源・保持範囲の確定（Issue #139）
type: spec
status: completed
related_ids:
  - SC-11
  - FR-15
  - ADR-0018
  - IADR-0029
  - IADR-0036
  - IADR-0046
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md (SC-11)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
---

# 仕様書: SC-11 構成バージョン履歴表示とデータ源・保持範囲の確定（Issue #139）

## 起点となる計画書（トレーサビリティ）

- 画面(SC): SC-11（構成ビューア）§(3) バージョン履歴
- 機能要求(FR): FR-15（構成の可視化・ドリフト検出）
- 関連 ADR: [[IADR-0046]]（本件のデータ源決定）／[[IADR-0029]]（構成情報 API 配置）／[[IADR-0036]]（可視化方式）
- Issue: #139（親 #122・調整 #123）／ SC-11 仕様書 未決事項 3

## 目的・背景

SC-11 の実効構成（#137）・ドリフト（#138）表示は実装済みだが、**構成バージョン適用履歴**（コミット ID・
適用日時・適用者・その時点のドリフト有無）が未実装で、その**正データ源・保持範囲が未決**だった
（SC-11 未決事項 3）。本作業でデータ源を確定（IADR）し、履歴 API と画面表示を実装する。

## データ源・保持範囲の決定（[[IADR-0046]]）

- **正データ源 = GitOps 層**（Git のコミット履歴 / ArgoCD リビジョン履歴）。プラットフォームのサービスに
  履歴ストアは新設しない（参照専用・GitOps のみで構成変更・依存最小の方針と一貫）。
- API（`/bff/admin/config/history`）は、現在バージョンと同じ注入経路で供給される `ConfigVersionOptions.History`
  を永続化せず新しい順に surfacing する。**保持範囲は GitOps 側が決定**。
- **縮退**: 履歴未注入（dev/compose）→ 現在バージョンの単一エントリ。現在バージョンも空 → 空一覧。
- 各エントリ: `gitCommit` / `appliedAt` / `appliedBy` / `hadDrift`（`bool?`、不明は画面「—」）。

## 対象範囲

### バックエンド
- Contracts: `ConfigVersionEntryDto` を追加。
- Infrastructure: `ConfigVersionOptions.History`（`ConfigVersionHistoryEntryOptions[]`）を追加。
  `IConfigInspectionService.GetVersionHistoryAsync` を追加し、注入 surfacing／縮退／空を実装（`AppliedAt` 解釈は共通化）。
- BFF: `GET /bff/admin/config/history`（ConfigViewer・404 秘匿・監査 `config.history.read`）を追加。

### フロントエンド
- `sc11-config/ConfigViewerPage.tsx`: `/admin/config/history` を独立取得し、`HistoryView`（新しい順の表・
  ドリフト有無列・0件/縮退表示）を §(3) として追加。取得失敗時は履歴領域のみ縮退。

### ドキュメント
- [[IADR-0046]] 新設、SC-11 画面仕様書 未決事項 3 を解決、SC-11 テスト仕様書に T-14〜T-18 追加、
  FR-15 機能仕様書へ `/history` を追記。

## 受け入れ基準

- [x] 履歴データ源・保持範囲の判断が IADR（[[IADR-0046]]）に記録され、SC-11 未決事項 3 が解決している。
- [x] 構成バージョンと履歴が画面で参照できる（新しい順・短縮コミット・適用日時・適用者・ドリフト有無）。
- [x] API 側の縮退（注入／現在バージョン単一／空）が単体テストで検証されている。
- [x] 履歴取得の 404 秘匿・監査が検証されている。
- [x] `dotnet build`／BFF テスト全合格、frontend typecheck/lint/テスト/カバレッジ床維持。

## 非対象（#123 側）

- 実際に Git ログ／ArgoCD リビジョンから `Config:History` を供給する GitOps（Helm/パイプライン）配線は #123。
  本作業は API 契約・dev 縮退・画面・テストで完結する。
