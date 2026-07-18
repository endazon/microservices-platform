---
title: IADR-0069 構成バージョン履歴は現在バージョンと同一注入経路で Helm から env 配列供給する
type: impl-adr
status: Accepted
related_ids:
  - FR-15
  - SC-11
  - ADR-0018
  - IADR-0029
  - IADR-0046
author: claude
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md (§設計要素6)"
---

# IADR-0069: 構成バージョン履歴は現在バージョンと同一注入経路で Helm から env 配列供給する

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-15（構成情報 API・構成バージョン）／SC-11（構成ビューア §(3) バージョン履歴）／
  ADR-0018（コンポーザブル）
- 関連 ADR: [[IADR-0046]]（履歴の正データ源＝GitOps 層・非永続 surfacing。本 ADR はその注入配線）／
  [[IADR-0029]]（構成情報 API 配置・現在バージョン注入）
- Issue: #192（#123 の唯一の任意残項目を切り出し）。前提=#144（現在バージョン注入）／#139（API 契約・Options バインド）
- 関連仕様書: `docs/specs/20260718_issue-192_config-history-gitops-injection.md`

## コンテキストと課題

[[IADR-0046]] は構成バージョン**履歴**の正データ源を GitOps 層と決め、API は注入された
`ConfigVersionOptions.History`（新しい順スライス）を永続化せず surfacing する設計を確定した。#139/PR #189 で
API 契約・Options 型・縮退・並び順・SC-11 表示・単体テストまで完成しているが、**実際に Helm/GitOps から
複数エントリの履歴を供給する配線**（[[IADR-0046]] が #123 に委ねた項目）が未実装だった。

現在バージョンは #144 で `config.gitCommit/appliedAt/appliedBy`（values）→ `Config__GitCommit/AppliedAt/AppliedBy`
（BFF Deployment env）として注入済みである。履歴もこの経路の**拡張**として供給できる必要がある一方、
未注入の dev/compose を壊さない後方互換が要る。

## 決定

**履歴は現在バージョンと同一の注入経路（Helm values → BFF Deployment env）で供給し、新機構を追加しない。**
ASP.NET Core の構成配列バインド規約に合わせ、Helm チャートは `config.history`（リスト）を
`Config__History__<i>__{GitCommit,AppliedAt,AppliedBy,HadDrift}` の env として BFF に描画する。

- **values**: `config.history`（既定 `[]`）。各エントリは `gitCommit` / `appliedAt` / `appliedBy` / `hadDrift`。
- **描画**: `templates/deployment.yaml` の `configVersion` 有効ブロック（#144）内で `config.history` を range し、
  各エントリの設定済みフィールドのみ env 化する。ASP.NET は `Section__<index>__<Prop>` を配列要素として
  バインドするため、`ConfigVersionOptions.History[i]` に対応づく。
- **供給元**: `--helm-set config.history[i].gitCommit=<sha>`（および `appliedAt`/`appliedBy`/`hadDrift`）または
  環境別 `values-<env>.yaml`。実値の並びは供給側（CD 自動化／ArgoCD リビジョン・Git ログ）が決める。
- **縮退（後方互換）**: 既定 `config.history: []` は env を一切出さない → `History` 空 → 現在バージョン単一へ縮退
  （[[IADR-0046]] の既存挙動）。dev/compose は無変更。
- **`hadDrift`**: 注入時に判明していれば `"true"/"false"` を出し、不明なら env を出さない（`bool?` の null＝不明を
  保つ。過去ドリフトの遡及計算はしない・[[IADR-0046]]）。

## 根拠 / 代替案

- **同一注入経路の拡張を採る**理由: 履歴は現在バージョン注入の時系列スライスであり（[[IADR-0046]]）、
  新しい供給機構（サイドカー／Init コンテナ／専用 ConfigMap レンダラ等）は依存とバリエーションを増やす。
  values→env は #144 で確立済みで、`--helm-set` と `values-<env>.yaml` の両方で CD から上書きできる。
- **履歴ストア新設を採らない**: [[IADR-0046]] で不採用済み（第二の真実を作らない）。本 ADR は配線のみで、
  この制約を維持する。
- **実値 end-to-end（ライブ CD 供給）を本配線に含めない**: 実 ArgoCD リビジョン／Git ログからの自動履歴生成は
  稼働 CD・環境に依存する。配線（Helm→env→Options→API）は環境非依存に完成でき、`helm template` と Options
  バインドで検証できる。よって配線 PR を独立に発行し（`Refs #192`）、供給側（CD 自動化）は運用手順として
  文書化して後続／環境待ちとする。起こり得ない実値を CI 内で捏造しない。

## 影響

- Helm: `values.yaml` に `config.history`（既定 `[]`）を追加。`templates/deployment.yaml` の現在バージョン
  注入ブロックに履歴 env 描画を追加。
- 運用文書: `deploy/argocd/application.yaml`・`docs/operations/operations.md`・`docs/how-to/deployment.md` に
  履歴供給手順を追記。
- テスト: `helm template --set config.history[i].*` の描画アサーションと、`Config__History__N__*` env →
  `ConfigVersionOptions.History` → `GetVersionHistoryAsync` 複数エントリの Options バインド契約テスト。
- バックエンド実装は変更なし（#139 の Options 型・縮退で足りる）。[[IADR-0046]] を実効化し、#123 が委ねた
  注入配線を満たす。
