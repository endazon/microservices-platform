---
title: 作業仕様書 — 実効構成 API の自己申告へストレージ・Wiki 同期のポートとコネクタ（登録済みプラグイン）を足し、MCP サーバーを収集先に入れ、構成バージョン（gitCommit）を配備で注入する（#2118 段 1）
type: spec
status: done
related_ids: [FR-15, FR-16, FR-01, FR-13, ADR-0018, ADR-0024, ADR-0011, ADR-0014, ADR-0106, IADR-0029, IADR-0462, IADR-0533, IADR-0646]
author: claude
created: 2026-10-26
updated: 2026-10-26
issue: "#2118"
---

# 作業仕様書 — 実効構成 API の申告の拡充と構成バージョンの注入（#2118 段 1）

> 本仕様書は実装着手前に作成した（着手 2026-10-26）。親は #2047（指摘 M-21 の実効構成 API）。
> 段の分け方はコーディネーターの承認（2026-10-26）による。本 PR は (a)・(b)(i)・(c)・(d-1)。(b)(ii)・(d-2) は別 issue。

## 現状（origin/develop `b8e7aef7` で再確認）

- `AddPort(` の呼び出しは RetrievalService と LlmGateway だけで、`AddConnector(` は非テストで 0 件である。
  計画 `06_technical/10_composability-design.md` §5 の構成取得 API は、ポート（LLM・埋め込み・ベクトル DB・**ストレージ・Wiki 同期**）と
  **コネクタ一覧**（「登録済みコネクタと有効・無効状態（09 の管理情報を集約）」）を応答に含めると定める。
  - オブジェクトストレージを使うのは 6 サービス（conversion・datasource・document・graph・ingestion・wiki。`AddPlatformObjectStorage`）。
  - Wiki 同期は wiki-service の `WikiJsGraphQlClient`（`WikiJs:GraphQlEndpoint`）。
  - DataSourceService は IADR-0029 の時点で「コネクタは実行時データ（DB）のため静的申告の対象外」として存在申告だけをしていた。
- 構成バージョンの `config.gitCommit` は既定で空である。`deploy/argocd/application.yaml` の注記と運用文書は
  「ArgoCD ネイティブ Helm はビルド変数をパラメータへ自動展開しない」としているが、**これは誤り**である。
  Argo CD の文書（User Guide の Helm §Build Environment と Build Environment）は「Helm apps have access to the standard build environment
  via substitution as parameters」とし、`ARGOCD_APP_REVISION`（解決済みの完全なリビジョン）を挙げる。
  `k8s-local-up.sh` も `config.gitCommit` を渡していない。
- BFF の収集先（`Introspection__Services__*`）に mcp-server が無く、`services.mcp` に `grpcPort` が無い。
  McpServer は `AddPlatformIntrospection("mcp-server", …)` と `MapPlatformIntrospection()` を持つが、h2c リスナ（`AddPlatformGrpcListener`）を立てていない。

## 方針

### (a) ポート

- **`object-storage`**: 共有の `ObjectStorageExtensions.AddObjectStoragePort(IntrospectionBuilder, IConfiguration)` を足す。
  構成（`ObjectStorage` 節）を `AddPlatformObjectStorage` と同じ判定で読み、構成済みなら `S3ObjectStorageClient`、未構成なら
  `NullObjectStorageClient` を申告する。接続先は**エンドポイントのホスト:ポートだけ**（資格情報・userinfo・パスを載せない）。
  6 サービスの `AddPlatformIntrospection` に足す。
- **`wiki-sync`**: wiki-service に `WikiJsGraphQlClient` とエンドポイントのホスト:ポートを申告する。既定値（`http://wiki-js:3000/graphql`）は
  HttpClient の構成と同じ 1 か所から読む。
- 構造の試験: `AddPlatformObjectStorage` を呼ぶサービスの `Program.cs` は、必ず `AddObjectStoragePort` も呼ぶ（書き忘れを捕まえる）。

### (b)(i) コネクタ（狭い読み）

- DataSourceService が**登録済みのコネクタのプラグイン 4 種**（filesystem・wiki・saas・db）を `enabled: true` で申告する。
  種別名は各コネクタの定数 1 か所から取り、DI に登録した `IDataSourceConnector` の集合と一致することを試験で固定する。
- 🔴 **これは狭い読みである。** 計画の「有効・無効状態」は (i) では常に有効になり、自明である。
  まさにそれが、計画の意図は (ii)（SC-06 に登録したデータソースと有効・無効の集約）かもしれない理由である。
  (ii) は申告を要求ごとに組み立てる仕組みが要る。IADR-0029 のフォローアップ 2 に追記し、別 issue にする。
  計画への質問（どちらの読みか）はマージ後に起票する。**計画が答えるまで (ii) は作らない。**

### (c) 構成バージョン（gitCommit）

- `deploy/argocd/application.yaml` の `helm.parameters` に `config.gitCommit=$ARGOCD_APP_REVISION`（`forceString: true`）を足す。
  誤った注記（「自動展開しない」）を改める。`appliedAt` には相当するビルド変数が無いので、従来どおり CD が与える。
- `scripts/k8s-local-up.sh` の `[6/7]` の helm に `--set-string config.gitCommit=$(git rev-parse HEAD)` を足す。
  - 作業ツリーに変更があっても、HEAD が detached でも、実在するコミット ID である。
  - ［2026-10-26 改訂 / #2286 の監査］`git rev-parse HEAD` が失敗したら、**警告して渡さない**（空のまま起動する。空は作り物の値ではない）。
    初版は止めていたが、表示だけの項目で起動を止めるのは釣り合わない（`IMAGE_TAG` の上書きで checkout でない木からも起動し得る）。
    読めた値が sha の形でなければ止める。
  - PoC の起動器でも構成ビューアにコミット ID が出るようになる（PR 本文に書く）。
- 試験:
  - `application.yaml` に当該パラメータがあること（node）。
  - `k8s-local-up.sh` が `git rev-parse HEAD` の値を `config.gitCommit` に渡し、読めなければ警告して渡さず、形が違えば止まること（k8s-local-up.test.js）。
  - helm の描画で BFF の `Config__GitCommit` が渡した値になること（既存の描画の試験の有無を確かめ、無ければ足す）。
- 実装 ADR: IADR-0646（Argo CD のビルド変数で注入する。IADR-0029 のフォローアップ 3 の「CD が上書き」を改める）。

### (d-1) MCP サーバーを収集先へ

- McpServer に `builder.AddPlatformGrpcListener()` を足す（`Grpc:Port` が無ければ立てない）。
- helm: `services.mcp.grpcPort: 8081` と、BFF の `Introspection__Services__mcp-server`。compose も同じ（`Grpc__Port`・`expose`・BFF の env）。
- 既存の `IntrospectionGrpcDeploymentWiringTests` が「宛先は h2c ポート・grpcPort を宣言・Program が h2c リスナと面を持つ」を固定する（追加分も走査で拾われる）。
- MCP の**実効ツール一覧**の統合（d-2）は別 issue（契約の追加・申告の要求ごとの組み立て・SC-11 の表示）。

## 受け入れ基準

- [x] 6 サービスの自己申告に `object-storage` のポートが現れ、実装と接続先（ホスト:ポートだけ）が構成どおりである。未構成なら `NullObjectStorageClient`。資格情報を載せない
- [x] wiki-service の自己申告に `wiki-sync` のポートが現れる
- [x] datasource-service の自己申告に登録済みコネクタ 4 種が `enabled: true` で現れ、DI の登録と一致する（狭い読み (i)）
- [x] Argo CD の Application が `config.gitCommit` に `$ARGOCD_APP_REVISION` を渡す。`k8s-local-up.sh` が `git rev-parse HEAD` を渡し、読めなければ警告して渡さず、形が違えば止まる
- [x] mcp-server が BFF の収集先に入り、h2c リスナと grpcPort を持つ（配線の試験）
- [x] 修正前は上の試験が赤
- [x] 運用文書（構成バージョンの注入）・FR-15 の通信仕様書とテスト仕様書を改める。IADR-0029 のフォローアップ 2 に (b)(ii) を追記する

## 母集合（規則 9）

- `git grep -n "AddPlatformObjectStorage("`（6 サービス。+ 共有の拡張とその試験）→ 6 サービスとも直す。
- `git grep -n "AddPlatformIntrospection("`（14 サービス）→ 上の 6 と wiki・datasource・mcp だけ直す。他は対象のポートを持たない。
- `git grep -n "自動展開しない\|ビルド変数"` → `deploy/argocd/application.yaml` と `docs/operations/operations.md` §構成バージョンの注入。両方直す。
- `git grep -n "Introspection__Services__"` → helm の BFF・compose の BFF・試験。helm と compose に mcp-server を足す。
- `git grep -n "コネクタは実行時データ"` → DataSourceService の `Program.cs` の注記。直す。

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。
