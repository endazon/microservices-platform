---
title: 作業仕様書 — 本番の NetworkPolicy に、取引ユニット（AST）の名前空間から KB の読み手（検索）・書き手（文書の保存）への ingress の許可を values で足す（#1756）
type: spec
status: done
related_ids: [NFR-09, FR-03, FR-02, ADR-0125, ADR-0119, ADR-0085, ADR-0084, IADR-0513, IADR-0492, IADR-0500, IADR-0026, IADR-0076, IADR-0348]
author: claude
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0125_ast-kb-reader-static-project-policy-with-confidentiality-ceiling.md 決定 4（本番の NetworkPolicy は別件）・実測 10
  - planning:projects/microservices-platform/07_adr/ADR-0119_document-machine-client-own-docs-and-read-abac.md（AST の書き手は DocumentService へ書く）
issue: "#1756"
---

# 作業仕様書 — AST の名前空間からの ingress の許可（#1756）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。判断の記録は **IADR-0513** に置く。
> 計画は project-planning `b5b584f`（`origin/main`）を読んだ。AST は `origin/develop` `8c7205cc` を読んだ。基点は MSP `origin/develop` `bde4fdba`。
> 🔴 **稼働中のクラスタには何も実行しない**（`helm template` / `helm lint` と単体の試験だけ）。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-09**（多層防御の一部としての NetworkPolicy）。到達先は **FR-03**（検索）・**FR-02**（文書の取り込み）。
- 計画 ADR: **ADR-0125 決定 4**（本番の NetworkPolicy は別件。「本番で読み手を使うには、AST の名前空間からの ingress を許す NetworkPolicy が要る。本 ADR では定めない」）・同 実測 10・フォローアップ 3。
  **ADR-0119**（AST の機械クライアントの書き手が DocumentService へ `POST` / `GET /documents` / `PUT …/body` を発行する前提）。
- 起点 issue: **#1756**。関連 #1696・#1755・planning#712・AST#1078。

## 計画が決めていること・決めていないこと

- 計画 ADR-0125 決定 4 は NetworkPolicy を**定めない**と明記し、フォローアップ 3 で実装へ渡している。
- 書き手（`ai-stock-trading-kb-writer`）が DocumentService へ書くことは ADR-0119 が前提にしている（計画の決定済み）。
  よって「書き手を許すか」は計画の未決ではない。本件で決めるのは**ネットワークの穴の形**（どの Pod から、どの Pod の、どのポートへ）と既定値だけで、実装の判断に収まる。

## 現状（`bde4fdba` の実測）

| 事実 | 確かめ方 |
| --- | --- |
| `default-deny-ingress`（全 Pod の ingress を拒否）と `allow-intra-namespace`（同じ名前空間の Pod だけ）。別名前空間からの許可は edge（istio-system → bff / frontend / document）だけ | `templates/networkpolicy.yaml` |
| `networkPolicy.enabled` は本番既定 `true`、経路B（`deploy/local/values-local.yaml`）は `false` | values |
| RetrievalService・DocumentService は REST 8080 と east-west gRPC 8081（h2c）を持つ | `values.yaml` の `services.retrieval` / `services.document` |
| MSP の PeerAuthentication は `STRICT`（`mesh.mtlsMode`）。AST の名前空間のサイドカー注入は AST の chart の既定で無効 | `templates/istio-mtls.yaml`・AST `values.yaml` の `mesh.sidecarInjection` |

AST 側（`8c7205cc`）の呼び出し:

| 用途 | AST の Pod（ラベル `app`） | 宛先 | 口 | 資格 |
| --- | --- | --- | --- | --- |
| KB の読み手（取引判断の RAG） | `trade-decision-service` | `retrieval-service.microservices-platform:8080` | REST `POST /search` | `ai-stock-trading-kb-reader` |
| KB の書き手（収集記事の保存） | `information-collection-service` | `document-service.microservices-platform:8080` | REST `POST /documents`・`GET /documents`・`PUT …/body` | `ai-stock-trading-kb-writer` |
| KB の書き手（確定報告書の保存） | `report-service` | 同上 | 同上 | 同上 |

- AST の Pod のラベルは `app: <名前>-service` 1 つ（AST `templates/deployment.yaml` の Pod テンプレート。Deployment の selector と同じ値で、変えるとロールアウトが壊れるので安定）。
- AST の Namespace は chart が作り、ラベル `app.kubernetes.io/part-of: ai-stock-trading` / `environment: local-dev` を付ける。`environment` は本番でも `local-dev` で意味を持たず、`part-of` は誰でも付けられる。
  **`kubernetes.io/metadata.name`**（API サーバが名前から自動で付ける・書き換えられない）を使う。既存の edge の許可と同じ形。
- `information-collection` は検索の BaseUrl の枠も持つが、本番の既定は空で、AST の chart 自身が「情報収集は検索を配線していない」と書いている。読み手の許可には入れない。
- gRPC（8081）を AST は使わない（REST だけ）。

## 設計（正は IADR-0513）

1. values `networkPolicy.fromAst`: `namespace`（既定 `ai-stock-trading`）と `kbReader` / `kbWriter` の 2 つ。各々 `enabled`（**既定 false**）・`target`（`services` のキー）・`clients`（AST の Pod の `app` の値）。
2. 描く NetworkPolicy は 1 用途 1 枚（`allow-ast-kb-reader-ingress` / `allow-ast-kb-writer-ingress`）。
   - `podSelector`: `app: <target>-service`。
   - `from`: **`namespaceSelector`（`kubernetes.io/metadata.name`）と `podSelector`（`app In clients`）を同じ要素に置く（AND）**。
   - `ports`: `services.<target>.port`（REST）**だけ**。gRPC の `grpcPort` は開けない。値を 2 か所に書かない。
3. `networkPolicy.enabled=false` なら描かない（既存の外側の条件の中に置く）。
4. 描画で止める（`fail`）: 有効なのに `clients` が空・`namespace` が空・`target` のサービスが無効または `port` を持たない。黙って広い穴（名前空間全体）や空の穴に倒さない。
5. CI 用の `ci/ast-kb-ingress-values.yaml`（両方を有効にするだけ）。`check-deploy-manifests.js` が `ci/*.yaml` を走査して lint / template / kubeconform を回す。
6. 試験 `scripts/helm-ast-kb-ingress.test.js`（helm が無ければ落ちる）を CI の `static-checks-units` に足す。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| A1 | 既定（本番像）と values-local の描画は変わらない（AST からの許可は 1 枚も描かれない） | 前後の `helm template` の diff が空・試験の陰性対照 |
| A2 | 有効時、上の 2 枚がちょうどこの形（選ぶ先・from の AND・ポート 8080 だけ）で描かれる | 試験 |
| A3 | NetworkPolicy の評価（許可の和）で、呼び出し元の一覧が期待どおり（読み手 → 検索 8080 だけ通る・書き手 → 文書 8080 だけ通る・gRPC・他の Pod・他の名前空間・他のサービスは落ちる・名前空間の中は不変） | 試験の評価器 |
| A4 | 片方だけの有効化はその 1 枚だけを描く。`networkPolicy.enabled=false` なら描かない | 試験 |
| A5 | knob の追随（`namespace`・`clients`・`services.<target>.port`）と、`fail` の 4 条件 | 試験 |
| A6 | 変異（from の AND を OR に割る・ポートを外す）で試験が赤になる | 試験 |
| A7 | 運用仕様書 §AST の KB の読み手のポリシーの投入「本番の前提」が新しい values と前提（メッシュ参入）を指す | 文書 |

## 母集合（規則 9・10。`bde4fdba` 時点）

### 規則 9

走査: `git grep -nE "#1756|名前空間からの(通信|ingress)|本番の前提（別件）|AST 名前空間からの|別名前空間（AST）|NetworkPolicy.{0,40}(AST|取引ユニット)|(AST|取引ユニット).{0,40}NetworkPolicy"` と
`default-deny-ingress|allow-intra-namespace`（`src/ai-stock-trading`・`.ai-context/specs`・`CHANGELOG.md` を除く）。

| 当たり | 追随 |
| --- | --- |
| `docs/operations/operations.md:523`（投入の注意 3「塞いでいる間は届かない」）・`:568`（本番の前提（別件）） | 追随（values で開ける手順と前提へ書き換える） |
| `.ai-context/adr/IADR-0492:134・142`（残余 2・残余 5 の追記「本番の NetworkPolicy は #1756」） | 追随（日付つき追記。本文は凍結） |
| `.ai-context/adr/IADR-0500:135`（残余 2「別件 #1756」） | 追随（日付つき追記） |
| `values.yaml:141`（edge の DENY は「別名前空間（AST）」に当たらない） | 追随しない（edge の DENY の射程の記述で、本変更でも正しい） |
| `default-deny-ingress` / `allow-intra-namespace` の他の当たり（SeaweedFS・合成監視・Vault・gRPC の Runbook・SeaweedFS の試験） | 追随しない（egress・名前空間の中の話で、別名前空間からの ingress を述べていない） |

### 規則 10

- `helm-private-notes-sync-authz.test.js` は既定と ci の values で描画全体を比べる箇所（`ON_LOCAL === OFF_LOCAL`）と、AuthorizationPolicy の数を見る箇所を持つ。本変更は既定で何も描かず AuthorizationPolicy を足さないので崩れない（実走で確かめる）。
- `check-deploy-manifests.js` は `ci/*.yaml` を走査で拾う。新しい ci の values も lint / template / kubeconform の対象になる（意図どおり）。
- 運用仕様書の注意 3 は「塞いでいる間は」を条件に書いていた。values を開けても、AST がメッシュに入らなければ STRICT の mTLS で届かない。条件を 2 つ（穴・メッシュ）に書き直す。

## 検証

- 前後の描画の diff（既定・values-local）が空。
- `node scripts/helm-ast-kb-ingress.test.js`・`node scripts/helm-private-notes-sync-authz.test.js`・`helm lint`（既定・ci の values）。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`check-trace-blocks`・`check-adr-numbering`・`check-doc-updated --base origin/develop`・`check-commit-messages`・`check-cross-repo-refs`・`check-plan-id-qualification`・`gen-knowledge-graph --check`。

## 範囲外

- ABAC のポリシー（#1755 で済み）。稼働中のクラスタへの適用。
- AST から LLM ゲートウェイ（`llmgateway-service:8080`）への ingress も同じ既定拒否で塞がれるが、#1756 の射程（KB）の外。IADR-0513 の残余に記す。
- 認証基盤（Keycloak。`platform-infra`）への到達は MSP の chart の外。
