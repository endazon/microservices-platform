---
title: IADR-0095 経路B Wiki.js を集約後 URL(wiki.localhost:50000) に載せ、OIDC は既存 wiki-js client の redirect 追加＋管理UI 設定手順で対応する（DB 保持のため manifest 自動化はしない）
type: impl-adr
status: Accepted
related_ids:
  - FR-13
  - UC-07
  - IADR-0020
  - IADR-0032
  - IADR-0091
  - IADR-0093
author: claude
created: 2026-07-21
updated: 2026-07-21
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/ (FR-13 Wiki)"
  - "../../planning/projects/microservices-platform/03_usecases/ (UC-07)"
---

# IADR-0095: 経路B Wiki.js の Keycloak OIDC 集約対応（最終・SSO 一括連携の締め）

- 状態: Accepted
- 日付: 2026-07-21
- 決定者: claude（実装）

## 起点・関連

- 計画: FR-13 / UC-07。Wiki.js 配備・ABAC ゲートウェイは [[IADR-0020]]。エッジ集約は [[IADR-0091]]、集約 route 追加の
  先例は [[IADR-0093]]（MinIO）。
- 仕様書: `docs/specs/20260721_issue-353_wikijs-keycloak-oidc.md`。
- Issue: #353 子タスク 5（Wiki.js・最終）。番号採番: develop 最新 IADR max=0094（#366 マージ済）＋1 の **0095**。

## コンテキストと課題

realm に `wiki-js` client は既存だが、Wiki.js の OIDC 設定は **DB/管理UI 保持**（"Generic OpenID Connect" ストラテジ）で
**manifest 完全自動化不可**（コールバック `{siteUrl}/login/{strategyKey}/callback`・strategyKey は作成時生成）。また Wiki.js は
#357 のエッジ集約に未登録で `wiki.localhost:50000` へ到達できない。#356 集約に wiki を加え、集約後 URL で OIDC ログイン
できるようにする。制約は「本番/既存 byte 等価」「他 route 無改変」「fail-safe（未マッピングは最小権限）」「平文 secret 非コミット」。

## 決定

### 1. 集約 route を edge に新規追加（他 route 無改変）

[[IADR-0091]] の Traefik `admin:50000` に **wiki 用 Ingress を新規1ファイル**（`deploy/local/edge/admin-ingress-wiki.yaml`・
host `wiki.localhost`→`wiki-js:3000`・microservices-platform ns）追加し kustomization に 1 行足す。**既存 grafana/argocd/
minio 等の Ingress・ファイルは無改変**（[[IADR-0093]] の MinIO と同型）。

### 2. realm は redirect 追加のみ（ワイルドカード）

`wiki-js` client の `redirectUris`/`webOrigins` に `http://wiki.localhost:50000/*`（＋origin）を**追加**する。既存の
port-forward 用（`http://localhost:3001/*`・`http://wiki-js:3000/*`）は残す（後方互換）。**Wiki.js のコールバックパスは
strategyKey 依存で不定のため、既存同様ワイルドカード `/*` で受ける**（固定パスにしない）。realm 差分は `wiki-js` client のみ。

### 3. Wiki.js OIDC は管理UI 設定手順で対応（DB 保持のため）

Wiki.js 2.x は OIDC を DB 保持（管理UI）で構成するため manifest 化しない。`deploy/local/wiki-oidc/README.md` に手順を置く:
Authorization/Token/UserInfo endpoints（Keycloak）・`client_id=wiki-js`・client secret（realm プレースホルダ・**管理UI 入力＝
リポジトリに平文コミットしない**）・**Site URL=`http://wiki.localhost:50000`**（コールバック基準）・**group/claim マッピング**。
realm import や MinIO の `mc` と同様の runtime 手順。

### 4. fail-safe・byte 等価・ABAC ゲートウェイ方針との関係

OIDC の group マッピングで**未マッピングユーザーは Wiki.js の最小権限グループ（Guests 相当）**へ割当（deny-by-default 寄り）。
ローカルログインは既定無効で OIDC 単一経路（[[IADR-0020]]）。変更は edge route 追加＋realm redirect 追加＋docs に限定し、
helm chart・`values.yaml`・Wiki.js Deployment は無改変（Ingress 既定 disabled の非公開運用は不変）。edge は経路B opt-in。

**ABAC ゲートウェイ方針との関係**: [[IADR-0020]] は Wiki.js への直接到達を塞ぎ WikiService の ABAC ゲートウェイを前段に
置く方針だが、その**非公開制約は本番系（Helm）限定**であり、[[IADR-0032]]（IADR-0020 追補）が **dev ホストでの直接公開を
既に許容**している（realm の既存 `http://localhost:3001/*` も同種の dev 直接到達）。本 edge route は **local 専用 opt-in
（`LOCALEDGE=1`）** で本番非公開の回帰ガード（IADR-0032）を壊さない。ゆえに ABAC ゲートウェイ方針からの逸脱ではない。

## 影響・トレードオフ

- Wiki.js が `wiki.localhost:50000` で集約到達でき、Keycloak SSO でログインできる（管理UI 設定後）。
- OIDC 設定は runtime（管理UI）＝適用前は既存の認証状態のまま（fail-safe）。dev の Wiki.js DB が消えれば再設定が必要。
- client secret は Wiki.js が DB 保持で env 注入できないため、grafana/minio のような k8s Secret 注入は行わない（realm
  プレースホルダ＋管理UI 入力で非平文を担保）。
- Site URL を集約 URL に設定するため、port-forward 単独では OIDC redirect が集約 URL を指す（edge 前提・grafana PR-2/
  MinIO と同性質・README 明記）。

## 代替案

- **固定コールバックパスを realm に登録**: Wiki.js の strategyKey が不定のため不可。既存同様ワイルドカードで受ける。
- **OIDC 設定を manifest/env 自動化**: Wiki.js 2.x は DB 保持で env 化不可のため手順化。
- **client secret を k8s Secret 注入**: Wiki.js が env から読まないため無効。realm プレースホルダ＋管理UI 入力に一元化。

## 追記（2026-07-26・Issue #385）— §2 の「port-forward 用」の対応付けを是正する

本 IADR の決定は不変（redirect 追加のみ・ワイルドカード）だが、**§2 の呼称に誤りがある**ため追記で是正する
（履歴不変の原則により本文は書き換えない。[[IADR-0032]] が [[IADR-0026]] §2 を Amends した前例と同型）。

§2 は既存 URL を「**port-forward 用**（`http://localhost:3001/*`・`http://wiki-js:3000/*`）」と記したが、
`3001` は **compose(dev) の host 公開ポート**（[[IADR-0032]] の `ports: 3001:3000`）であり、**k8s（k3d）の
port-forward は `svc/wiki-js 3300:3000`＝`http://localhost:3300`** で別経路である（§4 本文の「realm の既存
`http://localhost:3001/*` も同種の dev 直接到達」という記述の方が正しい）。この呼称の取り違えが
`deploy/local/wiki-oidc/README.md` / `deploy/local/README.md` へ転記され、非 edge・port-forward 単独時の
Site URL（`http://localhost:3300`）に対応する redirect が realm 未登録のまま「登録済み」と案内される不整合
（`invalid_redirect_uri`）を生んだ（#385）。

**是正後の対応付け**（`wiki-js` client の登録済み URL は経路ごとに別物）:

| 経路 | URL | 根拠 |
| --- | --- | --- |
| edge 集約（`LOCALEDGE=1`） | `http://wiki.localhost:50000/*` | 本 IADR §1・[[IADR-0091]] |
| k8s の port-forward（非 edge） | `http://localhost:3300/*` | #385。`port-forward svc/wiki-js 3300:3000` と同値 |
| compose(dev) の host 公開 | `http://localhost:3001/*` | [[IADR-0032]]（`ports: 3001:3000`） |
| in-cluster | `http://wiki-js:3000/*` | 本 IADR §2（既存） |

再発防止として `scripts/check-realm-constraints.js` が上表の URL 欠落を CI で機械検出する（#385）。
