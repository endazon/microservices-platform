---
title: 作業仕様書 — ルート README 整備と使い方/デプロイの how-to ドキュメント作成
type: spec
status: review
related_ids:
  - NFR
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - planning:projects/microservices-platform/06_technical/01_architecture-overview.md
related_specs:
  - ../../docs/README.md
  - ../../docs/operations/operations.md
  - ../../docs/tech/tech-requirements.md
  - ../../deploy/argocd/README.md
  - ../../deploy/bootstrap/README.md
  - ../../deploy/istio/README.md
  - ../../scripts/README.md
  - ../../src/platform/frontend/README.md
issue: "N/A（ユーザー依頼によるドキュメント整備）"
---

# 作業仕様書: ルート README 整備と使い方/デプロイの how-to ドキュメント作成

## 背景

リポジトリ直下に `README.md` が存在せず、初見の開発者/AI がプロジェクト概要・アーキテクチャ・
ローカル起動手順・主要ドキュメントの入口を把握できない。デプロイ手順は
`docs/operations/operations.md`・`deploy/*/README.md` に断片的に存在するが、環境ごと
（dev/stg・本番）の流れを通しで示す how-to ドキュメントがない。

## 目的・背景

- リポジトリルートに `README.md` を新設し、プロジェクト概要・アーキテクチャ概要・構成・
  ローカル起動手順（前提ツール/ビルド/テスト/実行）・主要ドキュメントへの入口を整備する。
- `docs/how-to/` に、ローカル開発フロー／テスト実行／環境ごとのデプロイ手順／GitOps・構成管理の
  運用（IADR-0046 の構成バージョン履歴の扱いを含む）をまとめた how-to ドキュメントを作成する。
- 記述は実在するスクリプト・設定・ドキュメントの実測に基づく（推測記述をしない）。

## 対象範囲

- 対象:
  - `README.md`（リポジトリルート、新規）
  - `docs/how-to/local-development.md`（新規）
  - `docs/how-to/deployment.md`（新規）
  - `docs/README.md`（`how-to/` の一覧・案内を追記）
- 対象外:
  - `docs/tech/tech-requirements.md` 等、既存の未記入必須仕様書を埋める作業（別スコープ）
  - CI/デプロイ設定自体の変更（記述のみ、設定は変更しない）

## 設計

一次情報として以下を実地調査し、記述内容の裏取りをした:

- ローカル開発: `deploy/docker-compose.yml`、`scripts/compose-up.sh`、`scripts/setup.sh`、
  `frontend/package.json`、`frontend/README.md`、`.devcontainer/devcontainer.json`
- ビルド/テスト規約: `CLAUDE.md`（技術スタック別ルール）、`.github/workflows/ci.yml`、
  `.github/workflows/frontend.yml`、`.github/workflows/frontend-tests.yml`
- デプロイ（GitOps）: `deploy/argocd/README.md`、`deploy/bootstrap/README.md`、
  `deploy/istio/README.md`、`deploy/helm/knowledge-platform/`、`docs/operations/operations.md`
- 構成バージョン履歴: `docs/adr/IADR-0046_config-version-history-source.md`、
  `docs/operations/operations.md`「構成バージョンの注入」節
- サービス構成: `src/Services/README.md`、`src/Services/*`、`src/Bff/`、`src/Shared/`
- ドキュメント配置規約: `docs/README.md`、`docs/templates/`

`README.md` の章立て: 概要 → アーキテクチャ概要（サービス一覧・Mermaid 図）→ リポジトリ構成 →
前提ツール → ローカル起動手順（ビルド/テスト/実行） → 主要ドキュメントへのリンク集
（docs/specs, docs/adr, docs/screens, docs/functional, docs/operations, docs/security, how-to）。

`docs/how-to/local-development.md`: セットアップ（clone/submodule/前提ツール）→ バックエンド
ビルド/テスト → フロントエンドビルド/テスト → compose 起動（`scripts/compose-up.sh` 推奨）→
サービス別エンドポイント確認 → よくある詰まり。

`docs/how-to/deployment.md`: 環境一覧（dev=compose / stg・prod=k3s+Istio+ArgoCD）→ GitOps 全体像 →
初回セットアップ手順（Secret → Istio → ArgoCD の順、`deploy/*/README.md` を参照・要約せず正本は
リンクに委ねる）→ サービス単位デプロイ・ロールバック → 構成バージョン履歴（IADR-0046・
`Config__GitCommit` 等の注入経路）→ ドリフト検出 → CI ゲート。

## 受け入れ基準

- [x] リポジトリ構成・各サービス/BFF/フロントエンド・ビルド/テスト/実行方法・デプロイ構成
      （compose / GitOps・ArgoCD 等）を実在のコード・設定から把握した
- [x] ルート `README.md` を新設し、概要・アーキテクチャ概要・構成・ローカル起動手順・
      主要ドキュメントへのリンクを記載した
- [x] `docs/how-to/` にローカル開発フロー・テスト実行・デプロイ手順（環境ごと）・GitOps/構成管理
      運用（IADR-0046 含む）を記載した
- [x] 記述内容が実在するコマンド・パス・設定と一致する（`node scripts/check-doc-links.js` で
      リンク切れがないことを確認）
- [ ]（メンテナ確認）PR レビューでの承認

## テスト方針

ドキュメントのみの変更のため実行系テストは対象外。以下で検証する。

- `node scripts/check-doc-links.js`（相対リンク切れ検査）
- 記載コマンド（`dotnet build` 等）が実際にリポジトリで通ることを手元で確認（可能な範囲）

## 計画書との差異

- 差異: なし。計画リポジトリの上流ドキュメント（`06_technical/01_architecture-overview.md` 等）に
  反する記述は行わない。

## 未決事項

- なし
