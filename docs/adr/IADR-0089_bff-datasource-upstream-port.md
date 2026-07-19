---
title: IADR-0089 BFF の datasource 上流ポートは「デプロイ manifest の Services__ 上書きで :8080 に揃える」（コード既定は不変）
type: impl-adr
status: Accepted
related_ids:
  - FR-01
  - FR-02
  - UC-04
  - SC-06
  - IADR-0039
author: claude
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/ (FR-01/FR-02 データソース管理)"
  - "../../planning/projects/microservices-platform/03_usecases/ (UC-04)"
  - "../../planning/projects/microservices-platform/05_screens/ (SC-06)"
---

# IADR-0089: BFF datasource 上流ポートの是正（デプロイ manifest 上書きで :8080 に揃える）

- 状態: Accepted
- 日付: 2026-07-20
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: [FR-01]/[FR-02]（データソース登録・管理）・[UC-04]・[SC-06]（管理者/運用者限定画面）。
- 関連 ADR: [[IADR-0039]]（datasource BFF 集約・管理系ロール限定＝本エンドポイントの設計）。
- 関連仕様書: `docs/specs/20260720_issue-342_bff-datasource-upstream-port.md`。
- Issue: #342（bug・priority:should）。#284（live 統合疎通）検証で発見。

## コンテキストと課題

live で `GET /bff/datasources` が ≈21 秒タイムアウト → 502 を返す。datasource-service は健全で、
throwaway pod から `http://datasource-service:8080/health/ready` は Healthy。NetworkPolicy も無い。

原因は BFF の上流宛先の不一致である。BFF は named HttpClient `"DataSourceService"` の `BaseAddress` を
`Program.cs` の `Configuration["Services:DataSourceService"] ?? "http://datasource-service:5002"` で決めるが、
`Services:DataSourceService` は appsettings にも manifest にも設定が無いため **コード既定 5002** が使われる。

実 Service ポートは **8080**。到達している他 downstream 7 件（Retrieval/AiAnalysis/Document/Authorization/
Wiki/Feedback/Dashboard）は BFF の `extraEnv`（Helm）/env（compose）で `Services__…: http://…:8080` に
上書きされているが、**`Services__DataSourceService` だけが上書きリストから欠落**していた。k8s ClusterIP は
8080 のみ公開のため 5002 への接続は SYN ブラックホール化し、OS の SYN 再送が尽きる ≈21 秒で
タイムアウトする（症状と一致）。

このリポジトリの確立パターンは「**appsettings/コード既定 = ローカル開発ポート（5001-5009）、各デプロイ
manifest が実 Service ポート `:8080` へ上書きする**」である。DataSource だけがこのパターンから漏れていた
（appsettings に項目自体が無く、後発の conversion/AST 系はコード既定を 8080 にしたが DataSource は旧
ローカルポート 5002 のまま、かつ manifest 上書きも未登録）。

## 決定

**欠落した `Services__DataSourceService: http://datasource-service:8080` の上書きを、他 7 downstream と同型で
デプロイ manifest（Helm `values.yaml` の `bff.extraEnv` と `docker-compose.yml` の BFF env）に追加する。
コード既定 `http://datasource-service:5002` は変更しない。**

## 代替案と却下理由

- **コード既定を 5002 → 8080 に変更**: 1 行で compose/k8s 両方が直る一方、
  (1) 全サービスの既定がローカルポートである確立パターンから DataSource だけを外す非対称を生む、
  (2) 純ローカル `dotnet run`（コンテナ外・datasource を 5002 で起動する開発フロー）を壊す恐れ、
  (3) 「デプロイ manifest で実効ポートを上書きする」という単一の是正点から外れる。
  よって却下。manifest 上書きの方がパターン一貫・低リスク・挙動等価。
- **appsettings.json に DataSourceService を追記**: 他サービスに倣うなら appsettings 既定もローカルポート
  であり、実効ポート上書きは manifest 側で行うのが本リポの流儀。appsettings への 8080 直書きは
  ローカル/デプロイの責務分離を崩すため採らない。

## 影響・互換性

- 挙動等価: 追加するのは欠落していた上書き 1 件のみ。他 7 downstream・コード既定・readiness 判定は不変。
- 後方互換: `Services:DataSourceService` を明示設定していない既存環境の実効挙動は「5002（不達）→ 8080（到達）」
  へ**是正**され、破壊的変更は無い。#275 ドリフト対象外（image 参照は不変）。
- 回帰防止: BFF 契約テストで `Services:DataSourceService` 設定時に named client の `BaseAddress` が
  当該値になることを固定。manifest は `helm template`・compose 静的検査で `:8080` 描画を確認。
