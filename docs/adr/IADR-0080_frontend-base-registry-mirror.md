---
title: IADR-0080 frontend の base イメージを docker.io 直参照から Google の Docker Hub プルスルーミラー（mirror.gcr.io/library）へ既定差し替えし、BASE_REGISTRY ARG で上書き可能にする
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - NFR
  - IADR-0056
  - IADR-0067
  - IADR-0068
  - IADR-0078
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0007_container-image-distribution.md"
---

# IADR-0080: frontend base イメージの非 docker.io 化（mirror.gcr.io 既定 + ARG 上書き）

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: claude（実装）／ endazon（マージ判断）

## 起点・関連

- 関連する計画書 ID（MSP・機械追跡）: **FR-14**（構成変更で完結する疎結合ユニット・SPA 配信入口）／**NFR**（運用・再現可能なビルド環境）
- 関連 ADR: [[IADR-0056]]（frontend の unit 構成・platform=アプリホスト）／[[IADR-0067]]（サービスイメージのビルド CI ゲート `images.yml`）／[[IADR-0068]]（#275 image-mapping ドリフト検査）／[[IADR-0078]]（frontend の k8s chart 配信・ローカルビルド常用化）
- Issue: MSP #325（本 issue・bug/infrastructure・priority:should）／派生元 #313（[[IADR-0078]]）・親トラッカ #284

## 背景・課題

Rancher Desktop（containerd / nerdctl）環境で `scripts/k8s-local-images.sh` による frontend イメージの
ローカルビルドが、base image を pull できず失敗する。`src/platform/frontend/Dockerfile` **だけが
docker.io を直参照**しており（`node:22-alpine` / `nginx:1.27-alpine`）、これが唯一の docker.io 依存だった。
frontend は元々 compose 専用だったが、[[IADR-0078]]（#313）で k8s chart 配信へ移行し、#284 のライブ
統合スタックでローカルビルドが常用になったため顕在化した。

### 根本原因（実測で特定）

当初は「docker.io 固有の資格情報問題」と見えたが、複数レジストリを実測して**真因はレジストリの
認証チャレンジ挙動**だと判明した。Rancher Desktop の nerdctl は、レジストリが匿名アクセスに対し
`401 Www-Authenticate: Bearer ...` チャレンジを返すと、（匿名 pull であっても）トークン取得のために
資格情報ヘルパを呼ぶ。この環境ではそのヘルパが `errorCode 255`（`exit status 22`）で失敗し、pull 全体が
落ちる。逆に、レジストリがチャレンジ**無し**（`200`／CDN への `307`）で manifest を返す場合はヘルパが
呼ばれず成立する。

実測（同一環境・決定的に再現）:

| レジストリ | 匿名 manifest 応答 | nerdctl pull |
| --- | --- | --- |
| `docker.io`（registry-1.docker.io） | 401 Bearer | **失敗**（errorCode 255） |
| `public.ecr.aws`（ECR Public・全 namespace） | 401 Bearer | **失敗**（errorCode 255） |
| `ghcr.io` | 401 Bearer | **失敗**（errorCode 255） |
| `mcr.microsoft.com`（.NET が成立） | 200（チャレンジ無し） | 成功 |
| `quay.io` | 200（チャレンジ無し） | 成功 |
| `registry.k8s.io` | 307（チャレンジ無し） | 成功 |
| **`mirror.gcr.io/library`** | **200（チャレンジ無し）** | **成功** |

この結果から、**docker.io を public.ecr.aws や ghcr.io に差し替えても解決しない**（いずれも 401 を
返し同じヘルパ失敗を招く）ことが確定した。解決には「node/nginx を提供し、かつ匿名アクセスに 401 を
返さない」レジストリが必要である。

### 制約: #275 ドリフト検査との結合

`scripts/check-image-mapping.js`（[[IADR-0068]]）は `deploy/docker-compose.yml` の `build.args` と
`scripts/k8s-local-images.sh` の `MAPPING` の args を**完全一致**で突合し、不一致（`args-mismatch`）を
CI 失格にする。したがって「compose 経由の build-arg でミラーを渡す」と CI も同じミラーを使うことに
なり、案1（CI は docker.io のまま・ローカルのみミラー）は build args を増やさずには実現できない。

## 決定

### 決定1: base イメージの既定を mirror.gcr.io の Docker Hub ミラーへ差し替える（案2）

`src/platform/frontend/Dockerfile` の 2 つの `FROM` を、`docker.io`（暗黙）直参照から
`mirror.gcr.io/library`（Google が運用する Docker Hub のプルスルーキャッシュ・匿名 pull 可・チャレンジ
無し）へ既定で差し替える。`BASE_REGISTRY` ARG を最初の `FROM` より前で宣言し、両ステージの `FROM` で
再利用する。

```dockerfile
ARG BASE_REGISTRY=mirror.gcr.io/library
FROM ${BASE_REGISTRY}/node:22-alpine  AS build
FROM ${BASE_REGISTRY}/nginx:1.27-alpine AS runtime
```

### 決定2: build args を増やさず、scripts / compose は変更しない

既定を Dockerfile に焼き込むため、ローカルビルド（`k8s-local-images.sh` / `docker compose build
frontend`）でも追加の `--build-arg` は不要。よって frontend の `MAPPING` エントリは 2 フィールドのまま、
compose の frontend build も args 無しのままで、#275 ドリフト検査は**緑を維持**する（新たな検査面を
作らない）。`BASE_REGISTRY` は override 用の逃げ道として残す（`docker.io/library`・社内 Harbor 等を
`--build-arg` で注入可能）。

## 根拠

- **真因への直接対処（実測検証済み）**: mirror.gcr.io はチャレンジ無し（200）で応答するため、破損して
  いる Rancher Desktop の資格情報ヘルパを呼ばずに済む。`node:22-alpine` / `nginx:1.27-alpine` の実 pull
  成功、および本 Dockerfile での end-to-end ビルド成功（node 22 pull → `npm ci` → `vite build` → nginx
  1.27 runtime）を同環境で確認した。
- **ミラーの信頼性・byte 等価性**: mirror.gcr.io は Google が運用する Docker Hub 公式イメージの
  プルスルーキャッシュで、Docker Hub と同一 digest を返す（生成イメージは byte 等価）。docker.io の
  匿名 pull レート制限を回避できる副次利得もある。
- **#275 ドリフト検査との結合**（上記）により案1は build args か CI 変化を招く。案2は build args ゼロで
  成立し blast radius が最小。
- **既存リポ方針との一貫性**: .NET サービスは全て非 docker.io（`mcr.microsoft.com`＝これもチャレンジ
  無しで成立）。frontend が唯一の docker.io 依存だった＝本 issue そのもの。非 docker.io・チャレンジ無しの
  公開レジストリへ揃えるのは既存パターンに合致する。

## 影響

- **挙動不変・後方互換**: 参照する base は同一の Docker Hub 公式イメージのミラーであり、生成イメージの
  内容・SPA 配信・実行時 config 生成は不変。`FROM` の取得元のみが変わる。
- **CI**:
  - `images.yml`（`docker compose build frontend`）: base の pull 先が docker.io → mirror.gcr.io に
    変わるのみ。GitHub runner から到達可・ビルド成立（Docker Hub レート制限回避で安定）。
  - `ci.yml`（backend のみ）/ `frontend.yml`・`frontend-tests.yml`（runner 上で npm 実行）: frontend
    Dockerfile base に非依存 → 影響なし。
  - `check-image-mapping.js`（#275）: build args 不変 → ドリフト検査は緑（自己試験 17 件 OK・実ツリー
    ドリフト 0 を確認）。
- **ローカル（経路B / Rancher Desktop）**: `bash scripts/k8s-local-up.sh` の frontend ビルドが docker.io に
  依存しなくなり成立する（本 issue の解消）。

## 代替案

- **案1: docker.io を既定に維持し、ローカルのみミラーを注入**。#275 ドリフト検査との結合により、
  両ローカル経路（`k8s-local-images.sh` と `docker compose build frontend`）を build args 無しでは
  カバーできず、また未検査分岐を frontend だけ特別に作ることになる。不採用。
- **公開ミラー `public.ecr.aws/docker/library` / `ghcr.io` への差し替え**: いずれも 401 Bearer チャレンジを
  返し、同じ資格情報ヘルパ失敗を招く（実測で確認）ため、真因を解決しない。不採用。
- **base をレジストリ digest で pin**: 再現性は上がるが本 issue（pull 可否）と直交し、既存のタグ運用
  （`22-alpine` / `1.27-alpine`）からの挙動変更になるため見送る。`BASE_REGISTRY` 上書きと両立するので
  将来別途検討可能。
