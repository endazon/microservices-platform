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

### 追加（PR #401 の claude-review 🟡 反映）

7. **誤りの発生源である [[IADR-0095]] §2 を是正する**。§2 は `3001` を「port-forward 用」と記しており、
   両 README はここから転記された。README だけ直しても ADR を参照した実装者/AI が同じ誤解を再導入するため、
   **履歴不変の原則に従い本文は書き換えず「追記」節で是正**する（[[IADR-0032]] が [[IADR-0026]] §2 を Amends した前例と同型）。
8. **経路別 URL の欠落を CI で機械検出する**。`redirectUris`/`webOrigins` の値そのものを固定する回帰テストが
   無いため、`3300` が将来消えても検出できなかった。`scripts/check-realm-constraints.js`（既に `ci.yml` の
   `realm-constraints` ジョブで実行）に**必須 URL の欠落検査**を追加する（新規スクリプト＋ワークフロー編集を避ける）。
   対象 client が realm に無い場合は検査しない（誤検出しない）。

## 実装

- [`deploy/keycloak/microservices-platform-realm.json`](../../deploy/keycloak/microservices-platform-realm.json):
  `wiki-js` client の `redirectUris` / `webOrigins` に `3300` を追加（追加のみ）。
- [`deploy/local/wiki-oidc/README.md`](../../deploy/local/wiki-oidc/README.md): 「注意」節の port-forward 記述を是正。
- [`deploy/local/README.md`](../../deploy/local/README.md): Wiki SSO 節の redirect 登録済み URL の記述を是正。
- [`docs/adr/IADR-0095_wikijs-keycloak-oidc.md`](../adr/IADR-0095_wikijs-keycloak-oidc.md): 「追記（2026-07-26・Issue #385）」
  節を追加し、§2 の「port-forward 用」という呼称を是正して経路別の対応表を置く（本文は不変）。
- [`scripts/check-realm-constraints.js`](../../scripts/check-realm-constraints.js): 検査2 として
  `REQUIRED_CLIENT_URLS`（経路別の必須 URL 表・IADR-0095 追記の表を単一情報源とする）と `collectMissingUrls` /
  `checkRealmUrlsText` を追加。自己試験も 7 → 12 件へ拡張。
- [`scripts/scripts.test.js`](../../scripts/scripts.test.js): 欠落検出・非対象 client の無視・実 realm との突合を単体テスト化。

## 検証

- `node scripts/check-realm-constraints.js --self-test` → 自己試験 12 件 OK。
- `node scripts/check-realm-constraints.js deploy/keycloak/microservices-platform-realm.json` → OK
  （255 文字超のフィールド・必須 URL の欠落なし）。
- **変異試験（ガードが実際に落ちることの確認）**: realm の複製から `3300` の 2 エントリを除去して同スクリプトを
  実行 → 欠落 2 件を検出し exit 1。ガードが機能することを実測で確認した。
- `node --test scripts/scripts.test.js` → 63 tests passed（58 → 63・欠落検出の単体テスト 5 件追加）。
- `node -e "JSON.parse(...)"` で realm JSON の妥当性を確認。
- `node scripts/check-doc-links.js` でドキュメントリンク切れなしを確認。
- realm の URL セットを固定する回帰テストは存在しない（`scripts/scripts.test.js` は制約長ロジックのみ検査）ため、
  実ブラウザでの SSO 疎通は稼働 k3d 依存＝**live**（Issue #385 も `priority:could` / live-tier と整理）。
