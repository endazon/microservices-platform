---
title: 構成バージョン履歴の GitOps 注入配線（Config:History の Helm 供給）（Issue #192）
type: spec
status: completed
related_ids:
  - FR-15
  - SC-11
  - ADR-0018
  - IADR-0029
  - IADR-0046
  - IADR-0069
author: claude
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md (SC-11)"
---

# 仕様書: 構成バージョン履歴の GitOps 注入配線（Issue #192）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-15（構成の可視化・構成バージョン・ドリフト検出）
- 画面(SC): SC-11（構成ビューア）§(3) バージョン履歴
- 関連 ADR: [[IADR-0046]]（履歴の正データ源＝GitOps 層・非永続 surfacing）／[[IADR-0029]]（構成情報 API 配置）／
  本作業の配線判断は [[IADR-0069]]
- Issue: #192（親 #123 の唯一の任意残項目を切り出し）／API 契約・SC-11 表示は #139（PR #189・[[IADR-0046]]）で実装済み／
  現在バージョン注入機構は #144（前提）で実装済み

## 目的・背景

構成バージョン**履歴**（`GET /bff/admin/config/history`）を、GitOps 層（実 Git コミット履歴／ArgoCD リビジョン
履歴）を正データ源として**実値の複数エントリ**で供給できるよう、`Config:History` の GitOps 注入配線を実装する。

- API 契約（`ConfigVersionEntryDto` / `GET /bff/admin/config/history`）・Options バインド
  （`ConfigVersionOptions.History`）・縮退・並び順・SC-11 表示は **#139/PR #189 で実装済み**であり、
  `ConfigVersionHistoryTests` で単体検証されている。
- 現在バージョン注入（`Config__GitCommit/AppliedAt/AppliedBy`）の Helm 配線は **#144 で実装済み**
  （`config.gitCommit/appliedAt/appliedBy` → BFF Deployment env）。
- **本件はデータ供給配線のみ**：#144 の現在バージョン注入の**拡張**として、Helm から複数エントリの履歴を
  `Config__History__<i>__{GitCommit,AppliedAt,AppliedBy,HadDrift}` env として供給する経路を追加する。

## 対象範囲

### 含む

- `deploy/helm/microservices-platform/values.yaml`: `config.history`（リスト、既定 `[]`）を追加。各エントリは
  `gitCommit` / `appliedAt` / `appliedBy` / `hadDrift`。
- `deploy/helm/microservices-platform/templates/deployment.yaml`: `configVersion` 有効サービス（BFF）に対し、
  `config.history` を range して `Config__History__<i>__<Field>` env を注入（#144 現在バージョン注入の直後、同一
  ブロック内の拡張）。ASP.NET Core の構成配列バインド規約（`Section__<index>__<Prop>`）に一致させる。
- `deploy/argocd/application.yaml` / `docs/operations/operations.md` / `docs/how-to/deployment.md`: 履歴の
  GitOps 供給手順（`--helm-set config.history[N].* ` もしくは `values-<env>.yaml`）を追記。
- 検証: `helm template` の描画アサーション（配線）＋ Options バインド契約テスト
  （`Config__History__N__*` → `ConfigVersionOptions.History` → `GetVersionHistoryAsync` 複数エントリ）。

### 含まない

- API 契約（`GET /bff/admin/config/history`）・SC-11 画面表示の変更（#139 で実装済み）。
- プラットフォームのサービスへの履歴ストア新設（[[IADR-0046]] で明確に不採用。第二の真実を作らない）。
- 保持範囲の決定（GitOps 側＝Git 履歴／ArgoCD 保持リビジョン数に委ねる。[[IADR-0046]]）。
- **実 ArgoCD/Git ログからの自動履歴生成（ライブ CD 供給）**。実値 end-to-end の達成はライブ CD 依存のため
  後続／環境待ちとし、本 PR は配線として独立に発行する（`Refs #192`。[[IADR-0069]] 「影響」参照）。

## 設計判断（[[IADR-0069]]）

- 履歴は**注入経路を現在バージョンと共有**する（新機構を足さない・[[IADR-0046]] 準拠）。ASP.NET の構成配列規約に
  合わせ `Config__History__<i>__<Prop>` を Helm から env で供給する。`--helm-set config.history[i].prop=...` /
  `values-<env>.yaml` の両方で上書き可能。
- 既定 `config.history: []`＝env を一切出さない → `ConfigVersionOptions.History` 空 → **現在バージョン単一へ縮退**
  （既存挙動・後方互換）。dev/compose に影響しない。
- `hadDrift` は注入時に判明していれば `"true"/"false"` を出し、未設定なら env 自体を出さない（`bool?` の null＝不明を
  保つ。遡及計算しない・[[IADR-0046]]）。

## 受け入れ基準

- [x] `config.history` に複数エントリを与えると Helm が `Config__History__<i>__{GitCommit,AppliedAt,AppliedBy,HadDrift}`
  を BFF Deployment env へ描画する（`helm template` で検証。`hadDrift` は `kindIs "bool"` の時のみ出力）。
- [x] その env 契約が `ConfigVersionOptions.History` に複数エントリとしてバインドされ、`GetVersionHistoryAsync` が
  実値の履歴を返す（縮退ではない）ことを Options 単体テスト（`ConfigVersionHistoryBindingTests`）で検証。
- [x] 注入方式が #144（現在バージョン注入）と整合し、その拡張として実装されている。
- [x] 未注入環境（dev/compose・`config.history: []`）では現在バージョン単一へ縮退する既存挙動が維持（回帰なし）。
- [x] [[IADR-0046]]（正データ源＝GitOps 層・非永続 surfacing）に違反しない（履歴ストア新設をしない）。
- [x] tech/infra いずれか該当の仕様と、必要な IADR を更新（本仕様書＋[[IADR-0069]]＋operations/deployment/argocd/FR-15）。
- [x] `helm template`／`dotnet build`／BFF テスト全合格、`dotnet format --verify-no-changes` 緑。
- [ ] 実値 end-to-end 履歴（実 ArgoCD リビジョン／Git ログからの**自動**供給）— ライブ CD 依存のため後続（下記「残作業」）。

## 残作業（受け入れに残す・PR で明記）

- **実値 end-to-end 履歴**：実 ArgoCD リビジョン／Git ログからの自動供給（ライブ CD）は環境依存のため後続。
  本 PR は配線（Helm→env→Options→API）を完成させ、供給側（CD 自動化）は運用手順として文書化する。
