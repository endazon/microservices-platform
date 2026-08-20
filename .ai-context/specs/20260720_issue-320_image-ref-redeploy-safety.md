---
title: 浮動タグ latest + IfNotPresent の再デプロイ安全性の是正・明文化（Issue #320）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0007
  - IADR-0078
  - IADR-0081
  - IADR-0088
author: claude
created: 2026-07-20
updated: 2026-07-20
related_specs:
  - "../adr/IADR-0088_image-reference-redeploy-safety.md"
  - "../adr/IADR-0081_frontend-base-registry-mirror.md"
  - "../adr/IADR-0078_frontend-k8s-serving.md"
  - "../../docs/operations/operations.md"
  - "../../deploy/helm/microservices-platform/values.yaml"
  - "../../deploy/docker-compose.yml"
  - "../../deploy/argocd/application.yaml"
  - "../../scripts/k8s-local-images.sh"
---

# 仕様書: 浮動タグ `latest` + `IfNotPresent` の再デプロイ安全性の是正・明文化（Issue #320）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): なし（配布物であるコンテナイメージの参照運用。プロダクト機能ではない）。
- 非機能要件(NFR): 運用性・信頼性・再現性（同名タグの再ビルドで stale image を掴まない＝再デプロイの
  確実性を担保する）。
- 関連 ADR: 配備物・GitOps は [ADR-0007]。方式判断は [IADR-0088](../adr/IADR-0088_image-reference-redeploy-safety.md)。frontend の k8s 配信は
  [IADR-0078](../adr/IADR-0078_frontend-k8s-serving.md)、ビルド base の非 docker.io 化は [IADR-0081](../adr/IADR-0081_frontend-base-registry-mirror.md)（#325）。
- Issue: #320（本 issue・enhancement・priority:could）。PR #319（#313/IADR-0078・SPA k8s 配信）の
  claude-review 🟡 派生。frontend 固有ではなく chart 全サービス共通の既存規約に関するもの。

## 目的・背景（As-Is）

Helm values の全サービスと一部 third-party が浮動タグ `tag: latest` ＋ `global.image.pullPolicy:
IfNotPresent` を既定にしている。同名タグ（`:latest`）で再ビルド・再 push しても既存 Pod/Node の
キャッシュにより再 pull されず**古いイメージが配信され続ける**リスクがある（frontend で顕在化）。

### 現状のイメージ参照の全体像（実ファイル洗い出し）

| 区分 | 対象 | 参照 | pullPolicy | 再デプロイ安全性 |
| --- | --- | --- | --- | --- |
| 自製（build） | `services.*`（13）＋`frontend` | `tag: latest`（テンプレは `\| default "latest"`） | `global.image.pullPolicy: IfNotPresent` | **CD が一意タグ/digest を渡すかに依存** |
| 自製 local | `k8s-local-images.sh` `TAG=latest` / `values-local` `registry: k3d-local` | `k3d-local/<img>:latest` | `IfNotPresent`（必須） | ローカル import で置換＋rollout で反映 |
| Third-party（固定済） | minio / qdrant / otel / prometheus / loki / tempo / grafana / keycloak / curl | 具体版タグ | `IfNotPresent` | 再現性あり（良好） |
| Third-party（浮動） | `requarks/wiki:2`（compose＋values wikijs） | major 浮動 | `IfNotPresent` | 是正対象（本 PR で `2.5` 固定） |
| Third-party（浮動・infra） | `postgres:16-alpine` / `redis:7-alpine` / `rabbitmq:3.13-management-alpine`（compose） | major/minor 浮動 | — | 本 PR 非対象（後述） |
| Third-party（placeholder） | embedding `cpu-1.5`（values コメント明示） | — | `IfNotPresent` | 稼働環境で固定（#303/IADR-0085） |
| ビルド base（FROM） | dotnet `{sdk,aspnet}:10.0` / `node:22-alpine` / `nginx:1.27-alpine`（`BASE_REGISTRY`） | 浮動 patch | — | 本 PR 非対象（後述） |

## 決定した方式（As-Is → To-Be）

方式判断は [IADR-0088](../adr/IADR-0088_image-reference-redeploy-safety.md) に記録。要点:

1. **自製イメージ**: 既定 `latest` を **CD 上書き用プレースホルダとして維持**する。再デプロイ安全性は
   「CD が一意タグ（git SHA）または digest（`@sha256:...`）を `--set services.<name>.tag=` で渡す」
   ことで担保し、**一意タグ運用下では `IfNotPresent` でも stale を掴まない**（新タグは必ず pull される）。
   これを `operations.md`・`argocd/application.yaml` に明文化する。`global.image.pullPolicy` は既に
   per-env で `Always` へ上書き可能だが、**既定を `Always` にしない**（local k3d の擬似レジストリ
   `k3d-local/...` を pull しようとして壊れる＋本番の pull 負荷が増える）。同名タグ再利用時の
   rollout 強制（`kubectl rollout restart` / pod template の checksum アノテーション）を運用指針に追記。
2. **wiki（third-party・evidence-backed 浮動）**: `requarks/wiki:2` → `2.5` に固定（compose＋values）。
   repo の実運用版が Wiki.js 2.5 であることが判明済みで、**挙動等価**の再現性向上。
3. **その他 third-party / ビルド base**: 本 PR では**タグを変更しない**。具体 patch タグをレジストリ
   照合できない作業環境で誤ピンは build（`images.yml`）/ 起動を壊すリスクがあり、infra の
   major-alpine 浮動はセキュリティ自動パッチの意図的選択でもある。**digest ピン**は CD/運用層の
   推奨事項として `operations.md` に明文化する（後続の CD 自動化＝ArgoCD image updater 等の領域）。

## 変更点（To-Be）

- `deploy/docker-compose.yml`: `ghcr.io/requarks/wiki:2` → `ghcr.io/requarks/wiki:2.5`。
- `deploy/helm/microservices-platform/values.yaml`: `wikijs.tag: "2"` → `"2.5"`。
- `docs/operations/operations.md`: 新節「イメージ参照と再デプロイ安全性」を追加
  （自製の一意タグ/digest 契約・`IfNotPresent` の安全条件・rollout 強制・third-party 固定/digest 方針・
  `Always` 既定を採らない理由）。
- `deploy/argocd/application.yaml`: services.<name>.tag のコメントを「一意タグ/digest 必須」に強化（挙動不変）。
- `docs/adr/IADR-0088_*.md` 追加・`docs/adr/README.md` 索引に 1 行追加。

## 影響範囲・非対象

- **#275 ドリフト検査（`check-image-mapping.js`）**: third-party（wiki 含む・compose では `image:`）は
  build 対象外で非検査。自製の MAPPING/Dockerfile/context/args は無改変＝ドリフト 0 を維持。
- **`images.yml` build**: ビルド base（dotnet/node/nginx）は無改変＝ビルド成立に影響なし。
- **realm / backend ロジック**: 一切触らない（イメージ参照の固定・明文化のみ）。

## 受け入れ基準（Acceptance）

1. CD が自製イメージを一意タグ/digest で適用する再デプロイ契約が `operations.md` と
   `argocd/application.yaml` に明文化されている（想定対応 1）。
2. `imagePullPolicy: Always` を選べる機構（`global.image.pullPolicy` 上書き）と本番/ローカルの
   トレードオフ（既定 `Always` を採らない理由）が明文化されている（想定対応 2）。
3. rollout 強制（`kubectl rollout restart` / checksum アノテーション）の運用指針が
   `operations.md` に追記されている（想定対応 3）。
4. 浮動 third-party のうち evidence のある wiki が具体版（`2.5`）に固定され、挙動等価が保たれる。
5. `helm template`（既定・wikijs 有効）が成功し、wikijs のイメージが `ghcr.io/requarks/wiki:2.5` を
   指す。`node scripts/check-image-mapping.js`（ドリフト 0）・`--self-test` が緑。既存 CI を壊さない。

## 検証（/verify 相当）

- `helm template` を既定 values で描画し、wikijs image が `2.5` を指すことを確認。
- `node scripts/check-image-mapping.js` と `--self-test` がともに緑（ドリフト 0）。
- `docker compose -f deploy/docker-compose.yml config` で compose が壊れていないことを確認（可能なら）。
