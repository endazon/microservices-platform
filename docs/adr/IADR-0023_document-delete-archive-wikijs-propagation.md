---
title: IADR-0023 文書の削除・アーカイブを Wiki.js へ伝播する（削除イベント新設＋status 拡張）
type: impl-adr
status: Accepted
related_ids:
  - FR-13
  - UC-07
  - ADR-0011
author: claude（実装）
created: 2026-07-07
updated: 2026-07-07
plan_refs:
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-07)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md"
---

# IADR-0023: 文書の削除・アーカイブを Wiki.js へ伝播する

- 状態: Accepted
- 日付: 2026-07-07
- 決定者: claude（実装）・Issue #88
- 関連: [IADR-0021](./IADR-0021_wiki-js-sync-graphql-push.md)（フォロー課題「削除・アーカイブ同期の未実装」を本 IADR で解消）、
  [IADR-0020](./IADR-0020_wiki-js-deployment-abac-gateway.md)、[IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md)

## コンテキストと課題

[IADR-0021] で Wiki.js が実コンテンツの実体を保持するようになったため、文書が削除・非公開化されても
Wiki.js 側ページが残り続けると、社内文書が外部システムに残存するリスクがある。既存の
`DocumentSyncConsumer` は `published`/`normalized` の `DocumentUpdated` のみ処理し、削除・アーカイブの
伝播経路が無かった（既存の設計ギャップ。Issue #88 スコープ4）。

上流のイベント設計も不足していた: `DELETE /documents/{id}` はイベントを発行せず、
`DocumentStatus` は `draft`/`normalized`/`published` のみでアーカイブ状態を表現できなかった。

## 検討した選択肢

1. **削除＝専用イベント新設・アーカイブ＝`DocumentUpdated` の status 拡張**（採用）
2. 削除もアーカイブも `DocumentUpdated` の status 拡張で表現（`status=deleted`）
3. 削除・アーカイブとも専用イベントを新設（`DocumentDeleted` / `DocumentArchived`）

## 決定

**選択肢 1 を採用する。**

- **削除（物理削除）**: `DocumentDeleted(DocumentId, DeletedAt)` イベントを新設する。
  削除後の文書は `Attributes` 等のペイロードを持たないため、`DocumentUpdated`（全メタデータ必須）への
  相乗りは不自然であり、専用イベントが正しい。`DELETE /documents/{id}` が削除確定後に発行する。
- **アーカイブ（非公開化・可逆）**: `DocumentStatus` に `archived` を追加し、`POST /documents/{id}/archive`
  が `DocumentUpdated(status=archived)` を発行する。アーカイブは文書メタデータを保持する状態遷移であり、
  既存の status 駆動同期の意味論に自然に載る。
- **WikiService 側の対応付け**:
  - `DocumentDeleted` → `DocumentDeletedConsumer`（新設）が Wiki.js の **`pages.delete` で実体を撤去**し、
    `wiki_svc` の同期メタデータ行も削除する。
  - `DocumentUpdated(status=archived)` → `DocumentSyncConsumer` が Wiki.js ページを
    **非公開化（`isPublished=false, isPrivate=true`）**し、メタデータを `WikiPageStatus.Archived`
    （定義済み・未使用だった状態）にする。再公開（`published` 再受信）で `Active` に戻る（可逆）。
  - 認可ゲートウェイ（`WikiEndpoints`）は `Active` 以外のページを一覧から除外し、個別取得は権限が
    あっても **404**（存在秘匿の意味論 [IADR-0009] を維持）。
- **冪等性**: 削除・アーカイブとも Wiki.js 上の正準パス（`doc/<DocumentId>`、`WikiPage.PathFor`）を
  DocumentId から導出するため、メタデータ未同期の ID・再配信に対して冪等（未存在ページは成功扱い）。
  失敗は例外送出し MassTransit のリトライ／デッドレター（`UseKnowledgePlatformRetry`）へ委ねる。

## 理由

- 削除は「文書が存在しない」ことの通知であり、メタデータ一式を運ぶ `DocumentUpdated` と意味論が異なる
  （選択肢 2 は空の `Attributes`/`Title` を強いる）。一方アーカイブは通常の状態遷移であり、既存の
  status 駆動 upsert 経路に載せる方が消費側の分岐が最小になる（選択肢 3 はイベント種を不必要に増やす）。
- 実体撤去（削除）と非公開化（アーカイブ）を分けることで、「外部システム残存防止」（撤去）と
  「可逆な非公開」（アーカイブ）の両要件を満たす。

## 結果

- 良い影響: 社内文書の Wiki.js 残存リスクを解消。アーカイブが可逆で、ゲートウェイの存在秘匿と整合。
- 悪い影響・トレードオフ: イベント契約が 1 種増える。Wiki.js の `pages.delete`/`pages.update`
  （unpublish）スキーマへの結合が増える（稼働 PoC で実測確認する — Issue #88 スコープ2）。
- 検証: `DocumentDeleteArchiveSyncTests`（削除・アーカイブ・冪等・再公開）、`WikiEndpointsAbacTests`
  （Archived の一覧除外・404）、`DocumentLifecycleEventTests`（イベント発行）で担保。

## 関連

- Supersedes: なし
- Superseded by: なし
- 作業仕様書: [20260707_issue-88-wikijs-verification-and-delete-sync](../specs/20260707_issue-88-wikijs-verification-and-delete-sync.md)
