---
title: 構成情報 API（実効構成の可視化・ドリフト検出） 機能仕様書
type: functional-spec
status: draft
related_ids:
  - FR-15
  - FR-14
  - SC-11
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-15)"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md (SC-11)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
---

# 機能仕様書: 構成情報 API（実効構成の可視化・ドリフト検出）

> Issue #118 監査で欠落が判明したため後追いで作成（実装は Issue #112 / #113 → PR #116 で完了済み）。
> SC-11 画面仕様書が「#112 実装時に作成」と定めていた機能仕様書に相当する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-15（現在有効なシステム構成と構成バージョンを読み取り専用 API で取得。宣言と実効の不一致を検出・警告。閲覧は管理者・運用者ロールに限定）
- 画面（SC）: SC-11（構成ビューア。画面実装は後続フェーズ）
- 計画書リンク: `02_requirements/01_requirements.md`、`05_screens/01_screens.md`、`07_adr/ADR-0018`
- 実装 ADR: [IADR-0029](../adr/IADR-0029_config-info-api-placement-and-drift-granularity.md)（BFF 配下への配置・ドリフト粒度）、
  [IADR-0030](../adr/IADR-0030_operator-role-and-config-viewer-policy.md)（運用者ロール `platform-operator` と ConfigViewer ポリシー）、
  [IADR-0009](../adr/IADR-0009_wiki-browsing-404-hides-existence.md)（存在秘匿の整合）

## 概要

FR-14 の宣言的構成（`pipeline.json`）に対し、**実行時に実際に有効な構成**（実効構成）を集約して返す
読み取り専用 API。宣言（Git）と実効の**ドリフト検出**を含む。独立サービス化せず **BFF 配下の管理 API**
として同居させる（IADR-0029。過剰分割回避）。

## 機能詳細

| 項目 | 内容 |
| --- | --- |
| 入力 | 各サービスの自己申告（イントロスペクション）エンドポイント（メッシュ内部限定）。収集先は `Introspection:Services`（構成キー＝pipeline.json の service 名 → ベース URL）で注入 |
| 処理 | BFF の `ConfigInspectionService` が自己申告を集約し、宣言（pipeline.json）と突合（`DriftDetector`） |
| 出力 | `GET /bff/admin/config` → `EffectiveConfigDto`（構成バージョン・段・イベント接続・ポート選択・コネクタ）、`GET /bff/admin/config/drift` → `DriftReportDto`（HasDrift・Findings[Kind/Severity/Target/Detail]）、`GET /bff/admin/config/history` → `ConfigVersionEntryDto[]`（コミット ID・適用日時・適用者・その時点のドリフト有無。新しい順） |
| 業務ルール | 認可・秘匿・監査（下記） |

### 認可・存在秘匿・監査（IADR-0030 / IADR-0009）

- 閲覧は **ConfigViewer ポリシー**（レルムロール `platform-admin` または `platform-operator` の OR）に限定。
- 非権限（無認証を含む）には **404** で応答自体を秘匿する。`RequireAuthorization` を使うと無認証が
  401 で短絡し存在が漏れるため、認可はハンドラ内で判定する。
- 取得操作は許可（granted）・拒否（denied）ともに監査ログへ記録する。

### ドリフト検出（IADR-0029）

- 宣言に無い購読（UndeclaredSubscription）、宣言と異なる実効値、自己申告に到達できない場合の
  `Unverifiable` 縮退を Findings として返す。
- 収集先が未設定・到達不能でも 500 にせず、検証不能を明示して返す（可用性優先の縮退）。

### 構成バージョン・履歴（IADR-0046）

- `Config__GitCommit / AppliedAt / AppliedBy` を GitOps（ArgoCD）適用時に環境変数で注入する
  （未注入時は空。注入配線は IADR-0029 フォローアップ）。
- **適用履歴**（`GET /bff/admin/config/history`）の正データ源は **GitOps 層**（Git のコミット履歴 /
  ArgoCD リビジョン履歴）。現在バージョンと同じ注入経路で供給する `Config__History__N__{GitCommit,AppliedAt,AppliedBy,HadDrift}`
  を、API は永続化せず新しい順で surfacing する（保持範囲は GitOps 側が決定）。履歴未注入（dev/compose）時は
  現在バージョンの単一エントリへ縮退し、現在バージョンも空なら空一覧。GitOps 注入配線は #123 が担当。

## 例外・エラー処理

| 条件 | 振る舞い |
| --- | --- |
| 無認証・非権限ロール | 404（存在秘匿。401/403 は返さない）＋監査 denied |
| 自己申告先に到達不能 | 該当サービスを Unverifiable として応答（200） |
| 宣言未供給（ローカル等） | 実効構成のみ返す。ドリフトは宣言なしとして警告 |

## 受け入れ基準

- [x] 管理者・運用者ロールで実効構成と構成バージョンを取得できる（compose 実測済み・Issue #118）
- [x] 一般ユーザー・無認証は 404 で存在を秘匿される（compose 実測済み・Issue #118）
- [x] 宣言と実効の不一致が Findings として返る
- [x] 取得の許可・拒否が監査ログに記録される

## 関連仕様

- 画面仕様書: [SC-11_configuration-viewer](../screens/SC-11_configuration-viewer.md)
- 通信仕様書: [openapi.yaml](../api/openapi.yaml)（`/bff/admin/config`・`/bff/admin/config/drift`）
- 機能仕様書: [FR-14_composability](FR-14_composability.md)
- テスト仕様書: [FR-15_config-info-api](../tests/FR-15_config-info-api.md)
- 作業仕様書: [20260707_FR-15](../specs/20260707_FR-15_config-info-api-introspection-drift.md) /
  [20260708_issue-113](../specs/20260708_issue-113_sc11-open-items-operator-role.md)

## 未決事項

- GitOps 構成バージョン注入と conversion / ingestion ワーカーの自己申告は IADR-0029 フォローアップとして追跡
