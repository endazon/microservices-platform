---
title: HPA・PodDisruptionBudget による水平スケール・可用性の実現（Issue #197）
type: spec
status: done
related_ids:
  - NFR
  - FR-14
  - ADR-0007
  - ADR-0008
  - IADR-0050
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: スケーラビリティ/可用性)
  - planning:projects/microservices-platform/07_adr/ADR-0007 (ArgoCD+Helm)
  - planning:projects/microservices-platform/07_adr/ADR-0008 (k3s)
---

# 仕様書: HPA・PDB による水平スケール・可用性の実現（Issue #197）

## 起点となる計画書（トレーサビリティ）

- NFR: スケーラビリティ「小〜中規模。HPA で水平スケール」／可用性「99.9% 以上（月間ダウンタイム約 43 分以内）」
- 機能要求(FR): FR-14（構成変更のみで完結＝GitOps 適用）
- 関連 ADR: ADR-0007（ArgoCD + Helm）／ADR-0008（k3s）
- Issue: #197

## 目的・背景

Helm チャートに HorizontalPodAutoscaler / PodDisruptionBudget が無く全サービス `replicas: 1` 固定で、
単一レプリカではローリング更新・ノード障害時に瞬断が生じ 99.9% の根拠を示せなかった。HPA/PDB を導入し、
可用性・水平スケールの実現手段を構成として提供する。

## 対象範囲

- 対象:
  - `templates/hpa.yaml`（新規）: `scaling.services` の各サービスに `autoscaling/v2` HPA（CPU 使用率）。
  - `templates/pdb.yaml`（新規）: 同サービスに `policy/v1` PDB（`minAvailable`）。
  - `templates/deployment.yaml`（変更）: HPA 対象は静的 `replicas` を出力しない（HPA が所有）。
  - `values.yaml`（変更）: `scaling`（enabled / services / hpa{min,max,targetCPU} / pdb{minAvailable}）を追加。
  - `docs/operations/operations.md`（変更）: 可用性・水平スケール節を追記（実現手段・段階適用・前提）。
- 適用対象サービス（段階適用）: ステートレスな要求処理系 bff / retrieval / authorization / aianalysis /
  document / datasource / dashboard / feedback / wiki / llmgateway（min 2 / max 4 / PDB minAvailable 1）。
- 対象外:
  - キュー駆動ワーカー conversion / ingestion（`worker: true`）: CPU HPA が不適。負荷実測（#196）後に
    キュー長ベース（KEDA 等）で別途検討。
  - ステートフル（minio / wikijs / postgres / qdrant）: 各自の可用性方針。
  - 監視アラート閾値・バックアップ/リストア・Runbook（#198）。負荷試験（#196）。

## 実装方針

1. `scaling` リスト駆動で HPA/PDB を生成（対象の増減はリスト変更＋GitOps のみ）。
2. HPA 対象の Deployment から静的 `replicas` を除去し、HPA と値が競合しないようにする。
3. HPA は `requests.cpu`（全対象で定義済み）を分母に CPU 使用率でスケール。metrics-server 前提（k3s 同梱）。
4. PDB は minAvailable 1（minReplicas 2 前提。単一レプリカのワーカーには付与しない＝ドレイン恒久ブロック回避）。

## 受け入れ基準（Issue #197）との対応

- [x] Helm チャートに HPA テンプレートがあり、対象サービスに HPA が生成される（`helm template` で 10 件）。
- [x] Helm チャートに PDB テンプレートがあり、対象サービスに PDB が生成される（10 件）。
- [x] `values.yaml` に autoscaling 相当（`scaling.hpa`）と適用対象リストがある。
- [x] HPA 対象の Deployment は静的 replicas を持たない（ワーカーは replicas 維持）。
- [x] 可用性の実現手段（レプリカ・PDB・プローブ・ロールアウト）を operations.md に明文化。
- [x] `helm lint` / `helm template` が通る。

## 検証

- `helm lint .` → 0 failed。
- `helm template kp .` → HorizontalPodAutoscaler 10 件・PodDisruptionBudget 10 件。document 等は Deployment に
  `replicas` 無し、conversion / ingestion は `replicas: 1` を維持。
- **回帰ガード（CI）**: `HpaPdbScalingTests`（`src/Tests/.../Deployment/`。helm 非依存の YAML 静的検査）で
  対象リスト 10 件・ワーカー除外・`replicas` 抑止条件・テンプレート存在を固定（claude-review #213 指摘対応）。

## 実装判断・フォローアップ

- HPA/PDB の適用対象を要求処理系に限定しワーカーを対象外とする判断は [IADR-0050](../adr/IADR-0050_hpa-pdb-scaling-scope.md) に記録
  （claude-review #213 指摘対応。計画「ワーカーはワーカー数で水平スケール」と整合）。
- 目標 CPU 値・min/max の妥当性は負荷試験（#196）で検証・調整する。
- Istio サイドカーの CPU 算入による HPA 判定への影響（`ContainerResource` 型切替の要否）は #196 で検証
  （operations.md「前提・確認事項」参照）。
- ワーカーのキュー長ベース自動スケール（KEDA 等）は #196 後に検討。
- 監視アラート・バックアップ・Runbook は #198 で整備する。
