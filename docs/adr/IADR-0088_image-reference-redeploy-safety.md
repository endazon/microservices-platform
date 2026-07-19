---
title: IADR-0088 イメージ参照の再デプロイ安全性は「自製=CD 一意タグ/digest 契約＋IfNotPresent 維持」「third-party=具体版固定」で担保する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0007
  - IADR-0078
  - IADR-0081
author: claude
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/ (NFR 運用性・信頼性・再現性)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0007 (CI/CD・GitOps)"
---

# IADR-0088: イメージ参照の再デプロイ安全性（自製=CD 一意タグ/digest 契約・third-party=具体版固定）

- 状態: Accepted
- 日付: 2026-07-20
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（運用性・信頼性・再現性＝同名タグの再ビルドで stale image を掴まず、
  再デプロイの確実性を担保する）。[ADR-0007]（配布物であるコンテナイメージ・GitOps）。
- 関連 ADR: [[IADR-0078]]（#313・frontend の k8s 配信＝stale が顕在化した箇所）／
  [[IADR-0081]]（#325・ビルド base の非 docker.io 化＝`BASE_REGISTRY`）。
- 関連仕様書: `docs/specs/20260720_issue-320_image-ref-redeploy-safety.md`。
- Issue: #320（enhancement・priority:could。PR #319＝#313/IADR-0078 の claude-review 🟡 派生。
  frontend 固有ではなく chart 全サービス共通の既存規約に関するもの）。

## コンテキストと課題

Helm values の全サービス（自製 `services.*`＋`frontend`）と一部 third-party（`requarks/wiki:2`）が
浮動タグ `tag: latest`／major 浮動タグ ＋ `global.image.pullPolicy: IfNotPresent` を既定にしている。
この組合せでは、同名タグでイメージを再ビルド・再 push しても既存 Pod/Node のキャッシュにより
再 pull されず**古いイメージが配信され続ける**リスクがある（frontend で顕在化）。

ただし現状は**機能上の不具合ではない**: 本番 CD（ArgoCD + Helm）は `services.<name>.tag` を Git 更新して
デプロイする設計であり（`argocd/application.yaml`）、そこで**一意タグ/digest** を渡していれば問題は
顕在化しない。課題は「その再デプロイ契約が暗黙で、chart 既定 `latest` のまま運用すると stale を掴む」
点である。全区分を一括で digest 固定すると影響が大きく、作業環境ではレジストリ照合もできない。

## 決定

**イメージ区分ごとに再デプロイ安全性の担保方式を分け、挙動等価・後方互換を保ったまま「契約の明文化」を
第一とする。**

### 1. 自製イメージ（build）— `latest` 既定を CD 上書き用プレースホルダとして維持

- 既定 `tag: latest` は**変更しない**（CD が上書きする placeholder）。再デプロイ安全性は
  「**CD が一意タグ（git SHA）または digest（`@sha256:...`）を `--set services.<name>.tag=` で渡す**」
  ことで担保する。**一意タグ運用下では `IfNotPresent` でも stale を掴まない**（新タグは必ず pull される）。
- `global.image.pullPolicy` は既に per-env で `Always` へ上書き可能。ただし**既定を `Always` にしない**:
  - ローカル（経路B）は `registry: k3d-local` の**擬似レジストリ**で、`Always` にすると存在しない
    レジストリを pull しようとして **Pod が起動不能**になる（local import + `IfNotPresent` が前提）。
  - 本番も毎回 registry へ問い合わせる pull 負荷が増える。一意タグなら `Always` は不要。
- 同名タグを再利用せざるを得ない場合（ローカル再ビルド・緊急）の**rollout 強制**手段
  （`kubectl rollout restart deployment/<name>` / pod template の checksum アノテーション。既存の
  `checksum/pipeline-config` と同型）を運用指針に明文化する。

### 2. Third-party イメージ — 具体版タグへ固定（evidence があるもの）

- `requarks/wiki:2` → `2.5`（compose＋values wikijs）。repo の実運用版が Wiki.js 2.5 であることが
  判明済みで、**挙動等価**の再現性向上。粒度は **minor 固定・patch は許容**（`2.5` は `2.5.x` 系列内の
  自動セキュリティパッチを受ける。実測 PoC は `2.5.314`＝`docs/tech/20260707_wikijs-poc-record.md`）。
  完全固定が必要なら CD 層で digest ピンする（下記 3）。
- 既に具体版で固定済みの third-party（minio/qdrant/otel/prometheus/loki/tempo/grafana/keycloak/curl）は
  現状維持。

### 3. その他 third-party / ビルド base — 本 IADR では変更せず、digest ピンを運用層の推奨として明文化

- `postgres:16-alpine`/`redis:7-alpine`/`rabbitmq:3.13-management-alpine`（compose infra）・ビルド base
  （dotnet `{sdk,aspnet}:10.0`・`node:22-alpine`・`nginx:1.27-alpine`）は**タグを変更しない**。
  作業環境で具体 patch タグをレジストリ照合できず、誤ピンは build（`images.yml`）/ 起動を壊す。
  infra の major-alpine 浮動はセキュリティ自動パッチの意図的選択でもある。
- **digest ピン**（`@sha256:...`）は再現性の最上位手段だが、per-arch・per-registry の解決と
  ミラー（`mirror.gcr.io`・#325/IADR-0081）整合の検証が必要なため、**CD/運用層の推奨事項**として
  `operations.md` に明文化する（後続の CD 自動化＝ArgoCD image updater / kustomize digest 運用の領域）。

## 却下した代替案

- **`global.image.pullPolicy: Always` を既定にする**: local k3d の擬似レジストリを壊し、本番 pull 負荷も
  増える。一意タグ運用下では不要。→ 却下（per-env 上書きの機構は残す）。
- **全 third-party/ビルド base を一括で具体版/digest 固定する**: 影響が大きく（レビュー可能性低下）、
  作業環境でタグ/digest を照合できず誤ピンで CI build を壊すリスク。→ 本 PR 非対象（段階化）。
- **自製 image に checksum ベースの強制 rollout をチャート既定で仕込む**: 一意タグ運用なら pod template
  が毎回変わり自動 rollout されるため不要。同名タグ再利用時の手段として運用指針に留める。→ 却下。

## 影響・非対象・トレーサビリティ

- **#275 ドリフト検査**（`check-image-mapping.js`）: third-party（wiki 含む・compose では `image:`）は
  build 対象外で非検査。自製の MAPPING/Dockerfile/context/args は無改変＝ドリフト 0 を維持。
- **`images.yml` build**: ビルド base 無改変＝ビルド成立に影響なし。
- **realm / backend ロジック**: 無改変（イメージ参照の固定・明文化のみ）。
- 挙動等価・後方互換: wiki の evidence-backed pin 以外は docs/コメントのみ。同一（互換）イメージを指し、
  pull 可能なレジストリを維持する。
