---
title: IADR-0038 文書閲覧の BFF 側 ABAC ゲーティングと本文サーバサイド取得
type: impl-adr
status: Accepted
related_ids:
  - SC-03
  - UC-01
  - UC-07
  - FR-06
  - FR-12
  - ADR-0004
  - ADR-0014
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md
---

# IADR-0038: 文書閲覧の BFF 側 ABAC ゲーティングと本文サーバサイド取得

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: SC-03（文書詳細／プレビュー）／ UC-01・UC-07 ／ FR-06（文書管理）・FR-12（変換）
- 関連 ADR: ADR-0004（ABAC 認可モデル）／ ADR-0014・ADR-0015（オブジェクトストレージ）／ [IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md)（存在秘匿）／ [IADR-0020](./IADR-0020_wiki-js-deployment-abac-gateway.md)（WikiService ゲートウェイの本文読み取り）／ [IADR-0024](./IADR-0024_object-storage-minio-buckets-and-access.md)（MinIO 共有クライアント）
- 関連する実装仕様書: `docs/screens/SC-03_document-detail.md`

## コンテキストと課題

SC-03 は正規化文書（Markdown）本文とメタデータを ID 指定で 1 件表示する。DocumentService の `/documents/{id}` は ABAC を適用しておらず（横断検索は RetrievalService 側で fail-closed 絞り込みを行うが、単一 ID 取得にはその経路が無い）、また `MarkdownUri` は `storage://` 参照で、本文を配信する HTTP API が存在しない。

決めること:
1. 単一文書取得に **どこで** ABAC を適用するか。
2. 権限外・不在の応答をどう扱うか（存在秘匿）。
3. 正規化 Markdown 本文を **どこから** 取得して SPA へ渡すか。

## 検討した選択肢

- **A. BFF 集約点で ABAC を適用し、本文はオブジェクトストレージからサーバサイド取得する**（採用）
  - 横断検索（`/bff/search`）と同じ「スコープ解決（AuthorizationService）→ 取得」の集約パターンを踏襲。本文読み取りは WikiService（[IADR-0020](./IADR-0020_wiki-js-deployment-abac-gateway.md)）と同じ storage 経路を再利用。
- B. DocumentService 自身に ABAC と本文配信エンドポイントを実装する
  - 各サービスへ認可・ストレージ依存を拡散させる。BFF 集約方針（CLAUDE.md）と重複し、サービス境界が太る。
- C. SPA が `MarkdownUri`（storage://）を直接取得する
  - ブラウザから storage:// は解決不能。かつ ABAC 前段を欠き権限昇格に繋がる。却下。

## 決定

1. **BFF 側 ABAC ゲーティング**: `/bff/documents`（一覧・`{id}`・`{id}/versions`・`{id}/content`）は、共通の `BffScopeResolver` で JWT からサーバ側スコープを解決し（クライアント指定 Scope は信頼しない・deny-by-default）、文書属性 `Attributes` を許可フィルタと照合する（`AbacEvaluator` と同一意味論：キー間 AND・値集合内 OR）。合致しない文書は列挙・取得ともに対象外とする。
2. **存在秘匿（404）**: スコープ外・不在・後段不調はいずれも 404（詳細・版・本文）または空配列（一覧）へ縮退し、「拒否」と「不在」を区別しない（[IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md)）。
3. **本文のサーバサイド取得**: 本文は ABAC 判定後に `IObjectStorageClient`（共有・[IADR-0024](./IADR-0024_object-storage-minio-buckets-and-access.md)）で `storage://` から読み取り `DocumentContentDto` で返す。ストレージ未配備（`NullObjectStorageClient`：`CanResolve=false`）・URI 未設定時はプレースホルダ本文へ縮退する（WikiService.`StorageMarkdownReader` と同一方針）。
4. **共通化**: 検索と文書閲覧で重複していたスコープ解決・属性抽出を `BffScopeResolver` に集約し、`SearchBffEndpoints` もこれを用いるようリファクタする（挙動不変、既存テストで担保）。

## 理由

- 横断検索と同じ集約点で認可を一元化でき、単一 ID 取得の認可欠落を塞げる。
- 本文取得は既存の storage 経路・共有クライアントを再利用し、新規バックエンド面を増やさない。
- 未配備環境ではプレースホルダへ縮退するため、dev/test でも画面が破綻しない。

## 結果

- 良い影響: SC-03 が権限内文書のみを安全に表示。SC-05（文書管理・#131）は本 read 経路（`/bff/documents`）を再利用でき、書き込み側のみ追加すればよい。
- 悪い影響・トレードオフ: BFF がオブジェクトストレージへ依存する（読み取り専用）。単一 ID 取得は「スコープ解決→取得→照合」で 2 往復になる（許容）。
- フォローアップ: SC-05（#131）で `/bff/documents` の書き込み側（作成・更新・メタ更新・公開・アーカイブ・削除）を追加する。

## 関連

- Supersedes: なし
- Superseded by: なし
