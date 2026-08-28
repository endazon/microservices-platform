---
title: GraphService をデプロイ経路へ載せる — Dockerfile はあるのに焼かれていない
type: spec
status: done
related_ids: [FR-17, UC-10, ADR-0034, IADR-0089, IADR-0067]
author: claude
created: 2026-08-22
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/02_requirements/ (FR-17 知識グラフ)
---

# 仕様書: GraphService をデプロイ経路へ載せる（#957）

> 本書は**着手前**に作成した。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-17**（知識グラフ）
- ユースケース（UC）: **UC-10**
- 関連 ADR: **ADR-0034** / **IADR-0067**（サービスイメージのビルド CI ゲート）/ **IADR-0089**（BFF 下流の上流ポート）

## 問題

**GraphService（#908 で新設）はどのデプロイ manifest にも入っていない。** 実装・テスト・CI ビルドは通り、Dockerfile も持つのに、**イメージが焼かれず compose にも k8s にも出ない。**

`/bff/graph/*`（#916a・#952）は `http://graph-service:8080` を呼ぶが、**そのホストはどの環境にも存在しない。**

### なぜ検査器が気付かないか

`check-image-mapping.js` は **compose の build 定義**と **`k8s-local-images.sh` の `MAPPING`** を突き合わせる。**両方とも「宣言された集合」であり、実在する Dockerfile が宣言されているかは誰も問うていない。**

### 同型の事故は 1 回目である（測定済み）

過去の新サービス追加（feedback / dashboard / frontend）は**いずれも Dockerfile と compose の build 定義を同一コミットで入れている**。AST 系 3 件は Dockerfile が submodule 側にあり母集合外。**GraphService が初かつ唯一。**よって規約どおり**記録に留め、検査器は足さない**（#957 のコメントが記録）。

## 🔴 設計判断: `AuthorizationService` の上流ポートをどう与えるか

GraphService は自分で `AuthorizationService`（`POST /authz/scope`）を呼ぶ。ここが `:5005` のままだと、**ABAC 解決が不達 → `Granted=false` へ縮退 → グラフが常に空**になる（#958 と同型）。

### 実効値の決まり方 —— **`appsettings.json` がコード既定に優先する**

```
環境変数（manifest） > appsettings.{Environment}.json > appsettings.json > Program.cs の ?? 既定
```

GraphService の現状:

| 層 | 値 |
| --- | --- |
| `Program.cs` の `??` 既定 | `http://authorization-service:5005` |
| `appsettings.json` | `http://authorization-service:5005` |
| `appsettings.Development.json` | `http://localhost:5005`（コンテナ外のローカル開発） |
| manifest | **無し** |

### 🔴 `Program.cs` の既定だけを 8080 に変えても効かない

`appsettings.json` が 5005 を供給するので**実効値は 5005 のまま**である。**「コード既定を直したから直った」は成り立たない。**

### 採る案: manifest で上書きする（IADR-0089 の確立パターン）

- **他の全サービス（Bff / AiAnalysis）が `AuthorizationService` に対して採っているのと同じ形**であり、#958 の是正案とも揃う
- `appsettings.Development.json` の `localhost:5005` が**コンテナ外のローカル開発の正**として機能し続ける（ここを壊さない）

**却下: `appsettings.json` を 8080 に書き換える。** ローカル/デプロイの責務分離を崩す（IADR-0089 が同じ理由で却下している）。

> ⚠️ **`check-bff-downstreams.js` は `appsettings.json` を読まない**（走査済み: 当該文字列 0 行）。実効値の模型が
> 「manifest 上書き ?? `Program.cs` 既定」で、**appsettings 層が抜けている**。現状は appsettings とコード既定が
> 同値なので結果が一致しているだけである。**#958 で母集合を広げる際にこの層も含めること。**本 issue では指摘に留める。

## 対象範囲

### 対象（走査で確定した必須要素）

| ファイル | 追加する内容 | なぜ必要か（走査の根拠） |
| --- | --- | --- |
| `deploy/docker-compose.yml` | `graph-service` サービス（build / expose 8080 / env / depends_on） | `images.yml` の matrix は compose の build 定義から `jq` で導出される |
| `deploy/create-multiple-dbs.sh` | `CREATE DATABASE graph_svc;` ＋ `ALTER ... OWNER TO kp;` | 🔴 DB は compose の外で作られる。**compose だけでは繋がらない** |
| `scripts/k8s-local-images.sh` | `MAPPING` へ 1 行 | `check-image-mapping.js` が compose と突合する |
| `deploy/helm/.../values.yaml` | `services.graph`（**キーは `graph`**） | テンプレートが `{{ $name }}-service` を組むので `graph` → `graph-service` |

### 対象外（走査で不要と確認した）

| 項目 | 不要な理由 |
| --- | --- |
| helm の Deployment / Service / HPA / PDB を個別に書く | `range .Values.services` の汎用ループ（`deployment.yaml:3` ほか） |
| NetworkPolicy の個別ルール | `podSelector: {}`（同 Namespace 内は全許可） |
| `pipeline.json` への登録 | GraphService は段をホストしない（`new PipelineOptions()`） |
| マイグレーション Job | 起動時に `db.Database.MigrateAsync()` |
| `images.yml` の matrix を手で編集 | compose から自動導出される |

## 受け入れ基準

- [ ] `docker compose -f deploy/docker-compose.yml config` が通り、build 対象に `graph-service` が現れる
- [ ] `node scripts/check-image-mapping.js` が EXIT=0
- [ ] `node scripts/check-bff-downstreams.js` が EXIT=0
- [ ] `helm template` 相当で `graph-service` の Deployment / Service が描画される
- [ ] 🔴 **CI の check-runs に `build (graph-service)` が現れる**（**これが完了条件。「足したから乗るはず」で終わらせない**）
- [ ] GraphService の実効 `Services__AuthorizationService` が両 manifest で 8080

## テスト方針

デプロイ manifest なので単体テストは持たない。**代わりに、機械検査と CI ジョブの出現で測る。**

| 測定 | 手段 | 「測れた」と言える条件 |
| --- | --- | --- |
| compose に載ったか | `docker compose config --format json \| jq '[.services\|to_entries[]\|select(.value.build)\|.key]'` | 一覧に `graph-service` が**在る** |
| image mapping | `node scripts/check-image-mapping.js` | EXIT=0（**変更前に落ちることも確認する**＝陽性対照） |
| helm | `helm template` の出力に `name: graph-service` | 描画される |
| 🔴 **CI に載ったか** | `gh api .../check-runs?per_page=100` | **`build (graph-service)` が列に在る** |

### 陽性対照

**`check-image-mapping.js` が「compose にあるが MAPPING に無い」を検出できることを、変更の途中段階で実測する**（compose だけ足した状態で走らせて落ちること）。落ちなければ、その検査器は本件について検出力を持たない。

## 計画書との差異

- 差異: なし

## 未決事項

1. `check-bff-downstreams.js` の実効値模型に `appsettings` 層が無い（#958 へ送る）
2. 「実在する Dockerfile が宣言されているか」の検査器（同型 2 回目で足す。今回は 1 回目）
