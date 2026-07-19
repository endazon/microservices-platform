---
title: 経路B（ローカル k8s dev・deploy/local）基盤インフラの永続化（Keycloak/Postgres を PVC 化・opt-in）（Issue #324）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0004
  - IADR-0066
  - IADR-0079
  - IADR-0081
author: claude
created: 2026-07-19
updated: 2026-07-19
related_specs:
  - "../adr/IADR-0081_local-k8s-infra-persistence.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "../adr/IADR-0079_infra-persistence-compose.md"
  - "../operations/operations.md"
  - "../../deploy/local/README.md"
---

# 仕様書: 経路B（ローカル k8s dev）基盤インフラの永続化（Keycloak / Postgres を PVC 化）（Issue #324）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): なし（運用・基盤インフラの永続化。プロダクト機能ではない）
- 非機能要件(NFR): 運用性・信頼性（**Pod 再起動/再作成でインフラ状態を失わないこと**）
- 関連 ADR: ADR-0004（認証＝Keycloak）。方式判断は [[IADR-0081]]。既存 [[IADR-0066]]（経路B の
  `emptyDir` 割り切り＝本 issue が見直す対象）／[[IADR-0079]]（compose 側の永続化・別レイヤの先例）。
- Issue: #324（本 issue・運用/dev・priority:should）。#282（PR #323）実装時のスコープ確認で経路B が
  明示除外されたため分離した派生タスク。ユーザーが経路B で Keycloak realm 消失に実際に遭遇している。

## 目的・背景（As-Is）

`deploy/local/`（＝経路B・ローカル k8s(k3d/Rancher) dev 環境）の infra は [IADR-0066] の割り切りで**永続化なし
＝`emptyDir`**（Pod 再起動で再 init）である。`deploy/local/README.md` にも「**永続化なし**: infra は emptyDir
（Pod 再起動で再 init）。dev 用途の割り切り。」と明記されている。このため:

1. **Keycloak**（`deploy/local/infra/keycloak.yaml`）: `start-dev --import-realm` で file H2 を **`/opt/keycloak/data`
   に持つが、当該パスをボリュームにマウントしていない**ため H2 データはコンテナの書き込み層に置かれ、**Pod 再起動の
   たびに realm が再 import され、管理コンソールで加えた runtime state（追加ユーザー・シークレット・セッション等）が
   失われる**。経路B を常用する運用で実害が報告されている（本 issue の直接動機）。
2. **Postgres**（`deploy/local/infra/postgres.yaml`）: `data` ボリュームが `emptyDir` のため、Pod 再起動で全アプリ
   DB（MSP の `*_svc` / AST の `*_svc`）と init が消え、`postgres-init` ConfigMap により再作成される（＝アプリデータ消失）。

compose 側（#282 / [IADR-0079]）は既に永続化済みだが、**#282 は経路B を意図的に対象外**とした。本 issue はその
フォローアップとして経路B の該当インフラを **PVC 化**し、再起動しても状態が保持されるようにする。クラスタには
`local-path` provisioner が既に存在する。

## スコープ（To-Be）

### 対象（PVC 化する）

| サービス | 現状 | 方式 | 保持されるもの |
| --- | --- | --- | --- |
| **Keycloak**（最優先） | `/opt/keycloak/data` 非マウント | PVC `keycloak-data`（1Gi・`local-path`）を `/opt/keycloak/data` にマウント（`start-dev` の file H2 を永続化） | realm＋runtime state（追加ユーザー・シークレット・セッション） |
| **Postgres** | `data` = `emptyDir` | PVC `postgres-data`（2Gi・`local-path`）を `/var/lib/postgresql/data` に | 全アプリ DB（MSP + AST） |

### 対象外（emptyDir 継続・意図的）

- **qdrant**: embeddings は Postgres/元ドキュメントから再生成可能な派生データで、dev では再 ingest がまれ。損失影響が
  低く、issue でも「必要に応じて qdrant 等」＝任意。同型の PVC を追加すれば拡張可能（[IADR-0081] に明記）。
- **rabbitmq / redis**: それぞれ queue / cache で揮発前提。**otel-collector**: stateless。

### 有効化（opt-in・既定オフ・後方互換）

- 新オーバーレイ `deploy/local/infra-persistence/`（kustomize）が base `deploy/local/infra` を参照し、PVC を追加、
  postgres/keycloak の Deployment に volume/volumeMount パッチを当てる（純加算）。
- `scripts/k8s-local-up.sh` は **`PERSIST=1`** で適用先を `deploy/local/infra` → `deploy/local/infra-persistence` に
  切り替える。**既定（env 未設定）は従来どおり emptyDir**（挙動不変・fail-safe。provisioner 不在環境で Pod Pending 化させない）。

## 受け入れ基準（Acceptance Criteria）

1. `PERSIST=1` 有効時、Keycloak の H2（realm＋runtime state）と Postgres のアプリ DB が **Pod 再起動/再作成で保持**される。
2. **既定（`PERSIST` 未設定）は現行 emptyDir と完全に同一挙動**（後方互換・CI 緑・fail-safe）。
3. `local-path` provisioner を利用（`storageClassName: local-path`）。
4. realm import 冪等性: 永続後は `--import-realm` が既存 realm をスキップ → **realm 更新反映手順**（PVC 削除で再生成
   ／partial import）を `deploy/local/README.md` と `docs/operations/operations.md` に明記。
5. **移行手順**（emptyDir→PVC 切替時、初回は PVC 空→import で再生成。既存 emptyDir データは元々揮発）を docs に明記。
6. 設計判断（opt-in / H2-on-PVC / compose との差異）を [IADR-0081] に記録。README（ADR 索引）は自分の 1 行のみ追記。
7. 検証: `kubectl kustomize deploy/local/infra` / `deploy/local/infra-persistence` が両方ビルド成功。既存 CI
   （#275 image-mapping ドリフト・realm-constraints・ci.yml self-test）を非回帰で緑。realm.json は無改変。

## 非スコープ

- Headlamp（#271）/ frontend base（#325・#326）/ edge / realm client 定義には触れない（`realm.json` の client 中身不変）。
- 本番像（`deploy/helm` / `deploy/argocd` / `deploy/docker-compose.yml`）は不変。
- observability overlay（Loki/Tempo。opt-in 別オーバーレイ）の PVC 化は本 issue の対象外（別途要すれば同型で拡張）。
- 稼働 k3d 上での実地の保持確認（実ブラウザで realm 残存を目視）は稼働環境依存＝live（PR で手順を明記し `Refs`）。

## 影響・リスク

- emptyDir→PVC 切替は Deployment の volume 差分＝ローリング更新が走る。初回 PVC は空のため realm/DB は再生成される
  （移行注記）。既存 emptyDir データは元々 Pod 生存期間のみで、失うべき恒久データは無い。
- Keycloak を独立 PVC（H2）に載せるため Postgres 起動順に結合しない（compose の `depends_on: healthcheck` 非依存）。
- `local-path` は Pod を単一ノードに固定する（RWO・dev 用途では問題なし）。
