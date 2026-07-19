---
title: BFF /bff/datasources の上流ポート是正（datasource-service:5002→8080 上書き欠落）（Issue #342）
type: spec
status: done
related_ids:
  - FR-01
  - FR-02
  - UC-04
  - SC-06
  - IADR-0039
  - IADR-0089
author: claude
created: 2026-07-20
updated: 2026-07-20
related_specs:
  - "../adr/IADR-0089_bff-datasource-upstream-port.md"
  - "../adr/IADR-0039_datasource-bff-admin-scope.md"
  - "../../src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DataSourceBffEndpoints.cs"
  - "../../src/platform/backend/Bff/Platform.Bff/Program.cs"
  - "../../deploy/helm/microservices-platform/values.yaml"
  - "../../deploy/docker-compose.yml"
---

# 仕様書: BFF `/bff/datasources` の上流ポート是正（Issue #342）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): [FR-01]/[FR-02]（データソース登録・管理）。
- ユースケース(UC): [UC-04]（データソース管理）。
- 画面(SC): [SC-06]（データソース管理画面・管理者/運用者限定）。
- 関連 ADR: [[IADR-0039]]（datasource BFF 集約・管理系ロール限定）。方式判断は [[IADR-0089]]。
- Issue: #342（bug・priority:should）。#284（live 統合疎通）検証で発見。

## 目的・背景（As-Is）

live 検証で `GET /bff/datasources` が datasource-service への上流接続で ≈21 秒タイムアウト → 502 を返す。
切り分けで以下が確認済み:

- datasource-service は健全（1/1 Running・`/health/ready`=Healthy・DB クエリ正常）。
- namespace に NetworkPolicy 無し。throwaway pod から `http://datasource-service:8080/health/ready`=Healthy。
- dashboard/retrieval/aianalysis/feedback は BFF から health で到達できるのに datasource だけ 21 秒失敗。

### 根本原因

BFF は `/bff/datasources` の上流を named HttpClient `"DataSourceService"` で解決する
（`DataSourceBffEndpoints.cs` の `CreateForwardingClient`）。その `BaseAddress` は `Program.cs` で

```
Configuration["Services:DataSourceService"] ?? "http://datasource-service:5002"
```

だが `Services:DataSourceService` はどこにも設定されていない（`appsettings.json` にも無い）ため
**コード既定のポート 5002** が使われる。

一方 datasource-service の実 Service ポートは **8080**（`values.yaml` の `services.datasource.port: 8080`・
`docker-compose.yml` は `expose: 8080`）。到達している他 downstream 7 件（Retrieval/AiAnalysis/Document/
Authorization/Wiki/Feedback/Dashboard）は BFF の `extraEnv`/compose env で `Services__…: http://…:8080` に
上書きされているのに、**`Services__DataSourceService` だけがこの上書きリストから欠落**している。

k8s ClusterIP `datasource-service` はポート 8080 のみ公開のため、5002 への接続は SYN がブラックホール化し
OS の SYN 再送が尽きる ≈21 秒でタイムアウト → BFF が 502 へ縮退する（`DataSourceBffEndpoints.cs` の
`IsTransient` 経路）。報告症状と一致する。

## 変更方針（To-Be）

「デプロイ manifest で各 downstream を `:8080` に上書きする」という確立パターン（他 7 サービス）から
DataSource だけが漏れている。パターンを踏襲し、欠落した上書きを補う **最小差分**とする。

- `deploy/helm/microservices-platform/values.yaml` の `bff.extraEnv` に
  `Services__DataSourceService: http://datasource-service:8080` を追加。
- `deploy/docker-compose.yml` の BFF service env に同値を追加（compose も同じ潜在バグ）。
- コード既定 5002 は純ローカル `dotnet run` 用に温存（他サービス既定もローカルポートのまま）。挙動等価。

代替案（コード既定を 8080 に変更）は採らない。理由は [[IADR-0089]] に記録。

## 影響範囲

- `deploy/helm/microservices-platform/values.yaml`（BFF extraEnv 1 行追加）。
- `deploy/docker-compose.yml`（BFF env 1 行追加）。
- テスト: `Platform.Bff.Tests` に datasource downstream 解決の回帰テストを追加。
- 他 downstream・既定挙動・後方互換に影響なし。#275 ドリフト対象外（image 参照は不変）。

## 受け入れ基準

- [x] `Configuration["Services:DataSourceService"]` が設定されると named client `"DataSourceService"` の
  `BaseAddress` がその値になる（契約/単体テストで固定）。
- [x] `helm template`（既定 values）で BFF Deployment に `Services__DataSourceService=http://datasource-service:8080`
  が描画される。
- [x] compose の BFF service env に同値が存在する。
- [x] `dotnet build`/`dotnet test`（platform backend）緑。既存 BFF テスト・#275 ドリフト・CI 緑。
- [x] 他 downstream の上書き（7 件）・コード既定・readiness 判定は不変（挙動等価）。

## テスト方針

- Program.cs のクライアント登録は本番ホスティングに閉じているため、`AddHttpClient` 登録相当を
  `IConfiguration` から `BaseAddress` を解決する形で契約テスト化し、`Services:DataSourceService` 設定時に
  8080 が採用されることを固定する（回帰防止）。
- manifest 側は `helm template` と compose の静的検査で `:8080` 描画を確認する。
