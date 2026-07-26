---
title: 非 edge（port-forward 単独）の Wiki.js OIDC が invalid_redirect_uri になる docs/realm 不整合の解消（Issue #385）
type: spec
status: done
related_ids:
  - IADR-0095
  - IADR-0091
  - IADR-0032
  - IADR-0020
  - FR-13
  - SC-04
author: claude
created: 2026-07-26
updated: 2026-07-26
related_specs:
  - "../adr/IADR-0095_wikijs-keycloak-oidc.md"
  - "../adr/IADR-0091_local-edge-aggregation-traefik.md"
  - "../adr/IADR-0032_wikijs-dev-exposure-opt-in.md"
  - "./20260725_issue-353_edge-oidc-redirect-uris-headlamp-spa.md"
  - "./20260725_issue-344_wiki-base-url-edge-alignment.md"
  - "../../deploy/keycloak/microservices-platform-realm.json"
  - "../../deploy/local/wiki-oidc/README.md"
  - "../../deploy/local/README.md"
---

# 仕様書: 非 edge port-forward 時の Wiki.js OIDC redirect 不整合の解消（Issue #385）

## 起点となる計画書（トレーサビリティ）

- 決定: [[IADR-0095]]（Wiki.js の Keycloak OIDC 連携）のフォローアップ。edge 集約は [[IADR-0091]]、
  dev の Wiki.js host 公開（compose `3001:3000`）は [[IADR-0032]]、ABAC ゲートウェイ前提は [[IADR-0020]]。
- 機能: FR-13（Wiki 閲覧）/ SC-04（Wiki アクセス）。
- Issue: #385（bug・documentation・`priority:could`）。PR #378 の claude-review が 🟢 として指摘した既存不整合。

## 背景と問題（現状の実値）

非 edge（`LOCALEDGE` 未使用・port-forward 単独）で Wiki.js の OIDC を使うときの案内と realm 登録が食い違う。

| 箇所 | 現状の値 |
| --- | --- |
| [`deploy/local/wiki-oidc/README.md:108`](../../deploy/local/wiki-oidc/README.md) | port-forward 時は Site URL を **`http://localhost:3300`** にする（「realm には旧 redirect も登録済み」と記載） |
| [`deploy/local/README.md:237`](../../deploy/local/README.md) | k8s の port-forward は **`svc/wiki-js 3300:3000`** → `http://localhost:3300` |
| [`deploy/local/values-local.yaml:108`](../../deploy/local/values-local.yaml) | 非 edge 時の `WIKI_BASE_URL` override 先も **`http://localhost:3300`** |
| [`deploy/keycloak/microservices-platform-realm.json:185-189`](../../deploy/keycloak/microservices-platform-realm.json) | `wiki-js` の `redirectUris` は `http://wiki.localhost:50000/*` / **`http://localhost:3001/*`** / `http://wiki-js:3000/*`。**`3300` は未登録** |

不整合の実体は **ポートの取り違え**である。`3001` は **compose（dev）の host 公開ポート**（[[IADR-0032]]・
`deploy/docker-compose.yml` の `ports: 3001:3000`）であって、**k8s（k3d）の port-forward ポート `3300` とは別経路**。
にもかかわらず両 README が `3001` を「port-forward 用の登録済み redirect」と説明していた。

**実害**: Site URL=`http://localhost:3300` だと Wiki.js が組み立てるコールバック
`http://localhost:3300/login/{strategyKey}/callback` が realm 未登録となり `invalid_redirect_uri` で
**非 edge・port-forward 単独の Wiki.js→Keycloak SSO が完了しない**。
edge 経路（`LOCALEDGE=1` / `wiki.localhost:50000`）は登録済みで正・不変。

## 方針（Issue の選択肢 (a) を採用）

Issue #385 の (a)「**3300 に統一**」を採る。(b)（3001 を正とする）を採らない理由:

- `3300` は既に k8s ローカル経路の既定として `deploy/local/README.md` / `values-local.yaml` /
  先行 spec（[[20260720_issue-344_frontend-wiki-url]] / [[20260725_issue-344_wiki-base-url-edge-alignment]]）に
  pin 済み。(b) は これら複数箇所と [[IADR-0032]] 由来の compose ポート意味論の両方を書き換える広い変更になる。
- `3001` は compose 経路の意味を持つポートであり、k8s port-forward に流用すると経路の区別が失われる。
- (a) は realm への **追加のみ**で、既存 URL（edge `wiki.localhost:50000` / compose `3001` / in-cluster `wiki-js:3000`）を
  すべて残す＝**後方互換**。

新規 IADR は不要（新たな設計判断は無く [[IADR-0095]] の適用漏れ修正のため `fix(IADR-0095)` で参照）。

## 受け入れ基準

1. `wiki-js` client の `redirectUris` に `http://localhost:3300/*` を、`webOrigins` に `http://localhost:3300` を追加する。
2. 既存の `http://wiki.localhost:50000/*`（edge・#357/IADR-0091）・`http://localhost:3001/*`（compose・IADR-0032）・
   `http://wiki-js:3000/*` は残す（後方互換）。他 client・他フィールドは無改変。
3. `deploy/local/wiki-oidc/README.md` の「注意」節が、port-forward 単独時の Site URL `http://localhost:3300` に
   対応する redirect が realm 登録済みであること、および `3001` は compose 経路であることを正しく述べる。
4. `deploy/local/README.md` の Wiki SSO 節が「port-forward 用 = `localhost:3001`」という誤記を止め、
   k8s port-forward は `3300`・compose は `3001` と区別して述べる。
5. realm JSON が妥当で、`scripts/check-realm-constraints.js`（varchar(255) ガード・Issue #18）が green。
   description は 255 文字以内。
6. アプリコード・本番 chart・`values-local.yaml` は無改変。CI / gitleaks green。

## 実装

- [`deploy/keycloak/microservices-platform-realm.json`](../../deploy/keycloak/microservices-platform-realm.json):
  `wiki-js` client の `redirectUris` / `webOrigins` に `3300` を追加（追加のみ）。
- [`deploy/local/wiki-oidc/README.md`](../../deploy/local/wiki-oidc/README.md): 「注意」節の port-forward 記述を是正。
- [`deploy/local/README.md`](../../deploy/local/README.md): Wiki SSO 節の redirect 登録済み URL の記述を是正。

## 検証

- `node scripts/check-realm-constraints.js deploy/keycloak/microservices-platform-realm.json` → OK。
- `node -e "JSON.parse(...)"` で realm JSON の妥当性を確認。
- `node scripts/check-doc-links.js` でドキュメントリンク切れなしを確認。
- realm の URL セットを固定する回帰テストは存在しない（`scripts/scripts.test.js` は制約長ロジックのみ検査）ため、
  実ブラウザでの SSO 疎通は稼働 k3d 依存＝**live**（Issue #385 も `priority:could` / live-tier と整理）。
