---
title: IADR-0050 HPA/PDB の適用対象はステートレス要求処理系に限定し、キュー駆動ワーカーは対象外とする
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0007
  - ADR-0008
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR: スケーラビリティ/可用性)"
  - "../../planning/projects/microservices-platform/06_technical/05_observability-otel-prom-loki.md (運用・スケール)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0008 (k3s: HPA で水平スケール)"
---

# IADR-0050: HPA/PDB の適用対象はステートレス要求処理系に限定し、キュー駆動ワーカーは対象外とする

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（スケーラビリティ「HPA で水平スケール」／可用性「99.9%」）
- 関連 ADR: ADR-0007（ArgoCD + Helm）／ADR-0008（k3s。HPA で水平スケールを明記）
- 関連仕様書: `docs/specs/20260710_issue-197_hpa-pdb-availability.md`、`docs/operations/operations.md`（可用性節）
- Issue: #197

## コンテキストと課題

Helm チャートは全サービス `replicas: 1` 固定で HPA/PDB が無く、可用性 99.9% の根拠を示せなかった（#197）。
HPA/PDB を導入するにあたり、**どのサービスを自動スケール対象にするか**という内部設計判断が必要になる。
計画の技術検討（`06_technical` 運用・スケール）は「取り込み・変換は**ワーカー数で水平スケール**」と記す一方、
要求処理系（BFF/検索/認可等）のスケール方式は HPA（ADR-0008）を前提とする。この差異を実装へどう写すかを
決める必要がある。

## 検討した選択肢

1. **全サービスを一律 CPU-HPA 対象にする**: 一様で単純だが、キュー駆動ワーカー（conversion/ingestion）の
   スケール指標として CPU は不適（本来はキュー滞留＝処理待ち件数が指標）。CPU が低くてもキューが詰まる／
   CPU 高でもスループット限界でない、といった誤スケールを招く。
2. **要求処理系のみ CPU-HPA、ワーカーは対象外（本決定）**: 計画の「ワーカーはワーカー数で水平スケール」に
   整合。ワーカーは当面 `replicas` の手動調整（＝ワーカー数）で対応し、将来はキュー長ベース（KEDA 等）を検討。
3. **ワーカーにキュー長ベース自動スケール（KEDA）を今すぐ導入**: 最も適切だが KEDA 依存の追加・メトリクス
   配線・負荷実測を伴い、#197 の即応スコープを超える。

## 決定

**選択肢 2 を採用する。** HPA/PDB の適用対象は `values.yaml` の `scaling.services` に列挙した
**ステートレスな要求処理系 10 サービス**（bff / retrieval / authorization / aianalysis / document /
datasource / dashboard / feedback / wiki / llmgateway）に限定し、`minReplicas: 2` + PDB `minAvailable: 1`
とする。**キュー駆動ワーカー（conversion / ingestion＝`worker: true`）は CPU-HPA の対象外**とし、`replicas` の
手動調整（ワーカー数）で水平化する。対象の増減は `scaling.services` リストの変更のみで行う。

## 理由

- **計画整合**: 計画は「取り込み・変換はワーカー数で水平スケール」と定めており、ワーカーを CPU-HPA から外し
  ワーカー数（`replicas`）で調整する本決定は**計画からの逸脱ではなく整合**である。
- **指標の適切性**: 要求処理系は CPU 使用率がスループット/レイテンシと相関しやすく CPU-HPA が有効。ワーカーは
  キュー滞留が本質的指標で、CPU-HPA は誤スケールのリスクがある。MassTransit 競合コンシューマにより
  ワーカーの水平化（replicas 増）は安全に可能。
- **段階適用**: 即応で要求処理系の可用性（99.9%）を確保しつつ、ワーカーのキュー長ベース自動スケール（KEDA 等）は
  負荷実測（#196）後に別途評価する（過剰投資を避ける）。

## 結果

- `deploy/helm/knowledge-platform/templates/hpa.yaml` / `pdb.yaml`（`scaling.services` 駆動）。
- `deployment.yaml`: HPA 対象は静的 `replicas` を出力しない（HPA が所有し値の綱引きを避ける）。
  ワーカー（conversion/ingestion）は `replicas` を維持。
- 回帰ガード: `HpaPdbScalingTests`（Helm YAML の静的検査。対象リスト・replicas 抑止・テンプレ存在）。
- `docs/operations/operations.md` に適用対象・前提（metrics-server・Istio サイドカーの CPU 算入）を明記。

## フォローアップ

- ワーカーのキュー長ベース自動スケール（KEDA 等）の評価（#196 負荷実測後）。
- HPA 目標 CPU 値・min/max・per-service オーバーライドの調整（#196 実測後）。
- Istio サイドカーの CPU 算入が HPA 判定に与える影響の確認と、必要なら `ContainerResource` 型への切替（#196 で検証）。

## 関連

- Supersedes: なし
- Superseded by: なし
