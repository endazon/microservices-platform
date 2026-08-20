---
title: frontend base イメージの非 docker.io 化（Rancher/nerdctl でのローカルビルド失敗の恒久修正・Issue #325）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0007
  - IADR-0067
  - IADR-0068
  - IADR-0078
  - IADR-0081
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR 運用・保守。「再現可能なビルド環境」は同区分からの類推)
  - planning:projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md (CI/CD・GitOps。コンテナイメージが配布単位)
related_specs:
  - "../adr/IADR-0081_frontend-base-registry-mirror.md"
  - "../adr/IADR-0078_frontend-k8s-serving.md"
  - "../adr/IADR-0068_image-mapping-drift-check.md"
---

# 作業仕様書: frontend base イメージの非 docker.io 化（Issue #325）

## 目的・背景

Rancher Desktop（containerd / nerdctl）環境で `bash scripts/k8s-local-up.sh` の frontend イメージ
ビルドが、base image を pull できず失敗する。`src/platform/frontend/Dockerfile` **だけが docker.io を
直参照**していたのが唯一の docker.io 依存で、[IADR-0078](../adr/IADR-0078_frontend-k8s-serving.md)（#313）で frontend を k8s chart 配信へ移行し
#284 のライブ統合スタックでローカルビルドが常用になったため顕在化した。

真因は「レジストリの 401 Bearer チャレンジ → 破損した資格情報ヘルパ（errorCode 255）呼び出し」で、
docker.io を public.ecr.aws / ghcr.io へ差し替えても解決しない（いずれも 401）。詳細と実測は
[IADR-0081](../adr/IADR-0081_frontend-base-registry-mirror.md) を参照。

## 変更内容

1. `src/platform/frontend/Dockerfile`: 2 つの `FROM` を `ARG BASE_REGISTRY`（既定
   `mirror.gcr.io/library`＝Google の Docker Hub プルスルーミラー・匿名/チャレンジ無しで pull 可）で
   パラメータ化する。`node:22-alpine` / `nginx:1.27-alpine` のタグは不変。
2. build args は増やさない（scripts / compose は無改変）。frontend の `MAPPING` エントリは 2 フィールドの
   まま＝#275 ドリフト検査の検査面を増やさない。
3. `docs/adr/IADR-0081` に設計判断を記録。

## 対象外

- `#282`（インフラ永続化・PVC / infra manifest・values の永続化箇所）
- `realm*.json`
- minio 等 infra イメージの registry 設定（`values.yaml` の `minio.registry` 等）

## 受け入れ基準

| # | 基準 | 検証 | 結果 |
| --- | --- | --- | --- |
| AC-1 | frontend イメージが docker.io に依存せず Rancher/nerdctl でビルドできる | 同環境で `nerdctl build -f src/platform/frontend/Dockerfile .` を実行 | ✅ end-to-end 成功（node 22 pull → `npm ci` → `vite build` → nginx 1.27 runtime → image load） |
| AC-2 | #275 ドリフト検査が緑 | `node scripts/check-image-mapping.js` / `--self-test` | ✅ 実ツリー ドリフト 0・自己試験 17 件 OK |
| AC-3 | doc リンクが緑 | `node scripts/check-doc-links.js` | ✅ 破損 0 |
| AC-4 | scripts 単体テストが緑 | `node scripts/scripts.test.js` | ✅ 58 件 pass |
| AC-5 | CI（本番）のビルドを壊さない・挙動不変 | mirror.gcr.io は Docker Hub 同一 digest（byte 等価）。CI は base pull 先が変わるのみ | ✅ 影響分析は [IADR-0081](../adr/IADR-0081_frontend-base-registry-mirror.md) 「影響」節 |

## 検証コマンド

```bash
node scripts/check-image-mapping.js && node scripts/check-image-mapping.js --self-test
node scripts/check-doc-links.js
node scripts/scripts.test.js
# 実ビルド（Rancher Desktop / nerdctl）
nerdctl build -f src/platform/frontend/Dockerfile -t msp-frontend-test:latest .
```
