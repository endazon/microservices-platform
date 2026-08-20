---
title: ローカル経路B のブラウザ OIDC 用に spa-web へローカル dev redirect URI(localhost:8081) を恒久追加する（Issue #340）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0004
  - IADR-0076
  - IADR-0078
author: claude
created: 2026-07-20
updated: 2026-07-20
related_specs:
  - "../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md"
  - "../adr/IADR-0078_frontend-k8s-serving.md"
  - "../../deploy/keycloak/microservices-platform-realm.json"
  - "../../deploy/local/README.md"
---

# 仕様書: spa-web へローカル dev redirect URI(localhost:8081) を恒久追加（Issue #340）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): なし（dev 環境の OIDC 配線。プロダクト機能ではない）。
- 非機能要件(NFR): 運用性・再現性（ローカル経路B のブラウザ OIDC 検証を、Keycloak 管理コンソールでの
  redirect URI 手動追加なしに再現可能にする）／セキュリティ（追加は **ローカル dev origin のみ**。本番/エッジ
  host の redirect は不変・ワイルドカードの過剰緩和なし）。
- 関連 ADR: ADR-0004（認証＝Keycloak）。前提は [IADR-0076](../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md)（決定3＝ブラウザ OIDC の issuer 統一・手順A）と
  [IADR-0078](../adr/IADR-0078_frontend-k8s-serving.md)（frontend の k8s 配信）。
- Issue: #340（本 issue・priority:should。#284＝IADR-0076・#313＝IADR-0078 のフォローアップ）。

## 目的・背景（As-Is）

ローカル経路B のブラウザ OIDC 検証（[`deploy/local/README.md`](../../deploy/local/README.md) の手順A / #313）では、
SPA(frontend-service) を `kubectl port-forward` でローカルポートに露出し、ブラウザで開いて Keycloak にサインインする。
SPA（`src/platform/frontend/src/foundation/auth/authConfig.ts`）は OIDC の `redirect_uri` を
`${window.location.origin}/callback` として送るため、**ブラウザで開いた origin（＝port-forward のローカルポート）が
`spa-web` client の `redirectUris` に登録されている必要がある**。

現状 `spa-web`（[`deploy/keycloak/microservices-platform-realm.json`](../../deploy/keycloak/microservices-platform-realm.json)）の
`redirectUris` は `http://localhost:3100/*` のみである。実運用の port-forward が **`localhost:8081`**
（`port-forward svc/frontend-service 8081:8080`）の場合、`redirect_uri=http://localhost:8081/callback` が未登録のため
Keycloak が **400（Invalid redirect_uri）** を返す。回避のため毎回 Keycloak 管理コンソールで redirect URI を手動追加
する必要があり、再現性が無い。

> [IADR-0076](../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md) 手順A は「realm 無改変」で成立すると述べているが、これは **per-session の realm 改変が不要**という意味で、
> 実体としては dev origin（`http://localhost:3100`）が realm に**恒久登録済み**であることに依存していた。本 issue は同じ
> 仕組み（恒久登録された dev origin）を、実運用のポート `8081` へ**追加のみ**で拡張する。設計方針の変更ではなく、既存
> 登録パターンの拡張である（新 IADR は起こさない）。

## スコープ（To-Be）

### 1. realm: `spa-web` にローカル dev origin `8081` を追加（追加のみ・本番不変）

`deploy/keycloak/microservices-platform-realm.json` の `spa-web` client を次のとおり**追加のみ**で更新する
（既存 `3100` は残す。他 client・description・権限は不変）。

| フィールド | 変更前 | 変更後 |
| --- | --- | --- |
| `redirectUris` | `["http://localhost:3100/*"]` | `["http://localhost:3100/*", "http://localhost:8081/*"]` |
| `webOrigins` | `["http://localhost:3100"]` | `["http://localhost:3100", "http://localhost:8081"]` |
| `attributes["post.logout.redirect.uris"]` | `"http://localhost:3100/*"` | `"http://localhost:3100/*##http://localhost:8081/*"`（`##` は Keycloak の複数値区切り） |

- 追加はすべて `http://localhost:<port>` のローカル dev URL に限定する。`*` 単体等の過剰なワイルドカードは使わない。
- 本番/エッジ host の redirect は**削除しない**（本変更に本番 origin は含まれない）。
- **Headlamp（`4466`）は既に `http://localhost:4466/*` を登録済み**のため変更不要。他の browser-login client
  （wiki-js=3001 / bff=5000）も既登録で対象外。

### 2. ドキュメント: `deploy/local/README.md`（手順A / #313）

- 「SPA(/settings) 到達」節の port-forward 例を実運用ポート `8081` に合わせ、`spa-web` に `8081`/`3100` が
  **恒久登録済み＝管理コンソールでの redirect URI 手動追加は不要**である旨と、別ポートを使う場合の追記先
  （`realm.json` の `spa-web`）を明記する。
- **反映タイミングの注記**: realm.json の変更は **新規クラスタ作成時の realm import で反映**される（既存クラスタは
  import がスキップ）。既存ローカル環境では管理コンソールで一度追加するか、クラスタ再作成（realm 再 import）で反映する。
- 手順A step 3 に、k8s 配信（#313）で確認する場合の port-forward ポート（`8081`/`3100`・いずれも登録済み）を補足する。

## 受け入れ基準（Acceptance Criteria）

1. `spa-web.redirectUris` に `http://localhost:8081/*` が含まれ、`http://localhost:3100/*` も**残っている**（追加のみ）。
2. `spa-web.webOrigins` に `http://localhost:8081` が含まれ、`http://localhost:3100` も残っている。
3. `spa-web.attributes["post.logout.redirect.uris"]` に `8081` が含まれる（`##` 区切りで `3100` と併存）。
4. 他 client（wiki-js / bff / ai-stock-trading-kb-writer / headlamp）の定義・description・権限は**バイト等価で不変**。
   本番/エッジ host の redirect は不変。
5. realm import 制約が緑（`node scripts/check-realm-constraints.js`。追加値はいずれも 255 文字未満・description 不変）。
6. `deploy/local/README.md` が「管理コンソール手動追加不要」「反映タイミング」「別ポートの追記先」を記載し、port が実手順
   （`8081`）と一致する。
7. 既存 CI が非回帰で緑: `check-realm-constraints`・#275 image-mapping ドリフト（`check-image-mapping.js`・本変更は image 非対象）・
   doc-links（`check-doc-links.js`）・commit-messages（scope=`NFR`）。realm JSON が妥当（`JSON.parse` 成功）。

## 非スコープ

- frontend/backend コード（`authConfig.ts` 等）の変更。SPA の `redirect_uri` 生成ロジックは不変。
- 他 client の権限・description・redirect/webOrigins（Headlamp=4466 は既登録で不変）。
- 本番像（`deploy/helm` の `values.yaml`・`deploy/argocd`・`deploy/docker-compose.yml`）の変更。realm export は
  dev/本番共通ファイルだが、追加するのはローカル dev origin のみで本番の到達性・セキュリティには影響しない。
- 新 IADR の起票（純 config/docs・追加のみ・既存登録パターンの拡張。設計判断なし）。
- `post.logout` の bare-origin 照合など既存 `3100` の挙動是正（本 issue は `8081` を `3100` と同一パターンで追加する
  ことに閉じる）。

## 影響・リスク

- 追加するのは `http://localhost:*` のローカル dev origin のみのため、本番の攻撃面・到達性は不変。既存 `3100` の
  経路も不変（後方互換）。
- realm.json の変更は既存クラスタには自動反映されない（`--import-realm` が既存 realm をスキップ）。README に反映手順を
  明記して運用差分を吸収する。
- **実ブラウザでの `8081` 経由の OIDC 実ログイン疎通**は稼働 k3d/k3s 依存＝live（#284 の live 分）。本 PR は realm 定義と
  手順の恒久化までを対象とし、`Refs #340` で残す。
