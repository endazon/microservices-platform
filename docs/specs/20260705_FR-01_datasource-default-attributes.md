---
title: 作業仕様書 — DataSourceService 原本への既定 ABAC 属性（機密区分）付与
type: work-spec
status: review
related_ids:
  - FR-01
  - FR-05
  - UC-04
  - ADR-0004
author: claude
created: 2026-07-05
updated: 2026-07-05
plan_refs:
  - "../../CLAUDE.md（トレーサビリティ規約）"
related_specs:
  - ../adr/IADR-0019_datasource-default-attributes.md
related_adrs:
  - IADR-0019 (データソースが原本へ既定 ABAC 属性を付与する)
  - IADR-0012 (Retrieval /search の fail-closed で ABAC を強制する)
  - IADR-0004 (ABAC の多値 allow-list・deny-by-default)
---

# 作業仕様書 — DataSourceService 原本への既定 ABAC 属性付与

- 起点 ID: FR-01 / FR-05 / UC-04 / ADR-0004
- 関連 Issue: #64（親 #48）
- 状態: review

## 背景・課題

#64（#48 横断監査の未実装トラッキング）の 1 項目。DataSourceService の同期トリガー
（`DataSourceEndpoints.cs` の `/{id}/sync`）が原本取得イベント `RawDocumentFetched` を
**`Attributes: []`（空）で発行**していた。

この属性はパイプライン
`DataSourceService → ConversionService(DocumentNormalized) → IngestionService → Qdrant → RetrievalService`
を通じて文書チャンクの ABAC 属性（`confidentiality` 等）として保持される。空のまま流れると：

- 文書に**機密区分が付与されない**。
- `RetrievalService` は fail-closed（IADR-0012）で ABAC を強制するため、機密区分を持たない文書は
  利用者の許可条件と突合できず**検索結果から除外**される。
- 結果として、パイプライン経由で取り込んだ文書が**実配備で検索に一切出ない**。

FR-01（データソース登録・同期・カタログ化）と FR-05（ABAC による可視制御）の実運用の前提が欠けていた。

## 調査結果

- `confidentiality` の許可値は AuthorizationService の属性辞書に準拠: `public / internal / confidential / restricted`
  （`AbacValidation` / `AbacValidationTests`）。
- 下流は `raw.Attributes.TryGetValue("confidentiality", ...)` を参照（`NormalizationService`）し、
  `RawDocumentFetchedConsumer` が `DocumentNormalized.Attributes` へそのまま引き継ぐ。属性の**発生源は原本取得のみ**。

## 方針（IADR-0019）

データソースは登録時に**既定 ABAC 文書属性 `DefaultAttributes`** を持ち、同期で発行する各
`RawDocumentFetched` へ写像する。`confidentiality` が未指定・空の場合は**フェイルセーフ既定値 `internal`**
で補完する（`public` の過剰公開でも `restricted` の過剰制限でもない社内基準）。

補完は**発行時に必ず通る `DataSource.GetEffectiveAttributes()` に一元化**し、`/{id}/sync` は
`DefaultAttributes` を直接コピーせず必ずこのアクセサ経由で属性を組み立てる。これにより本対応の
**マージ前から登録済みで `confidentiality` を持たない既存データソース**でも、同期時に `internal` が
確実に補完され fail-closed 除外が再発しない。詳細は IADR-0019 参照。

## 変更範囲

- `Domain/DataSource.cs`: `DefaultAttributes` 追加。`Create` に `defaultAttributes` 引数を追加し、
  `confidentiality` 欠落・空を既定値で補完（防御的コピー）。補完ロジックを単一のプライベートヘルパへ
  集約し、発行時に通る `GetEffectiveAttributes()` を追加。
- `Infrastructure/DataSourceDbContext.cs`: `DefaultAttributes` を jsonb 保管（既存 `Config` と同一の変換・比較器）。
- `Endpoints/DataSourceEndpoints.cs`: `CreateDataSourceRequest.DefaultAttributes` 追加、`sync` で
  `ds.GetEffectiveAttributes()`（フェイルセーフ適用済み）を原本イベントへ付与。
- `Migrations/`: `AddDataSourceDefaultAttributes`（jsonb、既存行は `{"confidentiality":"internal"}` を
  backfill）＋ Designer ＋ Snapshot 更新。
- テスト: `DataSourceTests`（Create の補完・保持・防御的コピー、`GetEffectiveAttributes` の補完）、
  `DataSourceSyncEndpointTests`（sync が既定/明示属性を原本へ付与、**既定属性が空の既存行でも `internal`
  を補完**する回帰）。

## 受け入れ基準

- [x] 機密区分未指定で登録したデータソースの `sync` は `confidentiality=internal` を持つ `RawDocumentFetched` を発行する。（`DataSourceSyncEndpointTests.Sync_WithoutExplicitAttributes_*`）
- [x] 明示した機密区分・部門などの属性はそのまま原本イベントへ付与される。（`DataSourceSyncEndpointTests.Sync_WithExplicitAttributes_*`）
- [x] **既定属性が空の既存（マイグレーション前）データソースでも `sync` は `internal` を補完して発行する**（fail-closed 再発防止）。（`DataSourceSyncEndpointTests.Sync_WithLegacyEmptyAttributes_*`、`DataSourceTests.GetEffectiveAttributes_*`）
- [x] `DefaultAttributes` は永続化され、再起動後も同期に反映される（jsonb カラム＋マイグレーション backfill）。
- [x] 既存テスト（Health / DataSources 一覧）に回帰がない。
- [ ] **CI 実走での検証**: 本作業環境は .NET SDK 未セットアップのためビルド・テストを実走できず、`ci` ワークフローに委ねる。

## スコープ外

- 実際のファイル取得は依然シミュレート（本作業は属性伝播に限定）。
- **`confidentiality` 許可値（`public / internal / confidential / restricted`）のバリデーションは本作業では行わない**。
  登録時に任意文字列がそのまま下流へ伝播しうる既知ギャップであり、AuthorizationService `/attributes/validate`
  との整合検証としてフォローアップ Issue で対応する（本 PR のスコープ外。IADR-0019「スコープ外」と同旨）。
- #64 の他項目（Istio / ArgoCD / MinIO / Embeddings 実体 / Stream ポート / SC 画面）は個別 Issue。
