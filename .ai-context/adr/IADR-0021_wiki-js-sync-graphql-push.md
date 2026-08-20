---
title: IADR-0021 Wiki.js への同期は GraphQL API push を採用する（ストレージ Git 同期は不採用）
type: impl-adr
status: Accepted
related_ids:
  - FR-13
  - UC-07
  - ADR-0011
author: claude（実装）
created: 2026-07-05
updated: 2026-07-07
plan_refs:
  - planning:projects/microservices-platform/03_usecases/01_usecases.md (UC-07)
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md
---

# IADR-0021: Wiki.js への同期は GraphQL API push を採用する

- 状態: Accepted（同期経路の実装は [IADR-0020] 段2 = 本 PR で完了。稼働 Wiki.js での PoC 実測はフォロー）
- 日付: 2026-07-05
- 決定者: claude（実装）
- 関連: [IADR-0020](./IADR-0020_wiki-js-deployment-abac-gateway.md)（Wiki.js 配備・WikiService 縮退）、
  ADR-0011、[IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md)

## コンテキストと課題

[IADR-0020] で Wiki.js を配備し WikiService を同期責務へ縮退すると決めた。`DocumentUpdated`（正規化 Markdown・
`Attributes`・`Tags` を含む）受信時に、Wiki.js のページ実体へ内容を反映する経路が必要である。Issue #66 は
方式（**ストレージ Git 同期** / **GraphQL API push**）を PoC で決定し IADR に記録することを求めている。

## 検討した選択肢

1. **GraphQL API push**: Wiki.js の管理 GraphQL API（`pages.create` / `pages.update` 等）へ、サービス
   アカウント（API トークン）で Markdown を push する。
2. **ストレージ Git 同期**: Wiki.js の Git ストレージモジュールを有効化し、外部 Git リポジトリへ Markdown を
   コミット、Wiki.js が pull して取り込む。

## 決定

**選択肢 1（GraphQL API push）を採用する。**

- WikiService の `DocumentSyncConsumer` が `DocumentUpdated` を受信し、Wiki.js の GraphQL エンドポイント
  （`http://wiki-js:3000/graphql`）へ、`DocumentId` を安定キーとするパス（例 `/<documentId>` または
  正規化 slug）で `pages.create` / `pages.update`（存在時は更新）を冪等に呼び出す。
- 認証はサービスアカウントの API キー（Wiki.js 管理で発行、秘密は環境変数/シークレット経由。コミットしない）。
- 送出内容は正規化済み Markdown 本文・タイトル・パスに限定する。ABAC 判定に用いる `Attributes` は
  **Wiki.js 側に権限として持たせない**（認可は [IADR-0020] のゲートウェイが単一真実源）。属性は WikiService の
  同期メタデータ（ゲートウェイのフィルタ用）として保持する。

## 理由

- **同期整合の即時性・可観測性**: API push はイベント駆動で即時反映でき、結果（成功/失敗）を同期側で
  直接ハンドリングできる（受け入れ基準②「更新で反映」に直結）。Git 同期は中間リポジトリ・Wiki.js の
  ポーリング/フック・マージ競合という追加の整合ポイントを増やす。
- **依存の最小化**: Git 同期は外部 Git リポジトリ・鍵管理・Wiki.js Git モジュール設定という運用面を新規に
  要する。API push は Wiki.js への HTTP 呼び出しのみで完結し、[IADR-0017] のネットワーク分離とも整合する。
- **冪等性**: `DocumentId` 由来の安定パスで upsert すれば、再配信（MassTransit のリトライ）に対して冪等。
  既存 `DocumentSyncConsumer` も `DocumentId` upsert 前提であり、意味論を引き継げる。

## 結果

- 良い影響: 反映が即時・失敗検知が容易・依存が少ない。認可は Wiki.js に持ち込まず単一真実源を維持。
- 悪い影響・トレードオフ: Wiki.js GraphQL スキーマ（バージョン差異）への結合が生じる。API キーの発行・保管・
  ローテーションが必要。**実際の GraphQL 呼び出し・エラー時再送・レイテンシは稼働 Wiki.js での PoC 実測が必要**
  であり、実コード置換は [IADR-0020] 段2 で行う（本 IADR は方式決定を確定）。
- 実装（本 PR = [IADR-0020] 段2）:
  - `IWikiJsClient` / `WikiJsGraphQlClient`: `pages.singleByPath` で既存を引き `pages.create`/`pages.update` を
    冪等呼び出し（path = `doc/<DocumentId>` の安定キー）。認証は Bearer（API キー）。
  - `IWikiContentReader` / `StorageMarkdownReader`: 正規化 Markdown を `MarkdownUri` から取得（http(s) 実取得・
    dev はプレースホルダ）。
  - `DocumentSyncConsumer`: 自前 DB 書き込み → Wiki.js push へ置換（属性は Wiki.js へ push せず、ゲートウェイの
    フィルタ用メタデータとして wiki_svc に保持）。同期失敗は例外を送出し `UseKnowledgePlatformRetry` に委ねる。
  - API キーのシークレット管理: compose の `WIKIJS_API_KEY` 環境変数・Helm の Secret `wikijs-sync`（key=apiKey）。
- 多層防御（表示制御の `isPrivate`。AI レビュー指摘への対応）:
  - ABAC は本システムが単一真実源だが、機密性の担保が**ネットワーク分離（[IADR-0017]）の単一防御線**にのみ
    依存する構成を避けるため、push 時に機密区分由来の**粗粒度な非公開設定**を Wiki.js へも伝える。
    `confidentiality=public` **以外（属性欠落を含む）は Wiki.js 上でも非公開**（`isPrivate=true`, deny-closed）。
  - これは ADR-0011 の方針（Wiki.js は表示制御に留め、ABAC の細粒度判定・正本は本システム）と整合する。
    属性集合そのものは引き続き Wiki.js へ持ち込まない（push するのは真偽値 1 つのみ）。NetworkPolicy が
    退行・誤設定されても public 以外が無条件公開にならない第 2 の防御線となる。
  - 検証: `DocumentSyncConsumerTests`（confidentiality→`isPrivate` 対応・属性欠落時の deny-closed）で担保。
- フォローアップ:
  - 稼働 Wiki.js での GraphQL スキーマ整合・エラー時再送・レイテンシの PoC 実測（本実装は `IWikiJsClient`
    背後に隔離しスキーマ差異を吸収しやすくしている）。**`isPrivate=true` のページをサービスアカウント
    （API キー）で本文取得（`singleByPath`）できるか**も PoC で確認する（取得不可なら認可プロキシは
    fail-closed で 404 = 安全側だが、正当な閲覧まで塞ぐため要調整）。
  - **文書の削除・アーカイブ（非公開化）に対する同期経路が未実装**（既存の設計ギャップ。AI レビュー指摘）。
    現状 `DocumentSyncConsumer` は `published`/`normalized` の `DocumentUpdated` のみ処理し、文書が削除・
    非公開化されても Wiki.js 側ページの撤去／非公開化・wiki_svc メタデータの `Archived` 化（`WikiPageStatus.Archived`
    は定義済みだが未使用）を行わない。本 PR で Wiki.js が実コンテンツの実体を保持するようになったため、
    社内文書が外部システムに残り続けるリスクが拡大している。`isPrivate` 多層防御で「public 以外は非公開」
    は担保されるが、**論理削除・撤去の伝播は別途フォロー課題**とする（削除/アーカイブ用イベントまたは
    `DocumentUpdated` の status 拡張に応じた `pages.delete`／非公開化・メタデータ `Archived` 化の追加）。

## フォローアップの結果（2026-07-07 追記・Issue #88）

- 稼働 Wiki.js 2.5.314 での PoC 実測を完了した。**`isPrivate=true` ページはサービスアカウント
  （fullAccess API キー）で `singleByPath` 本文取得が可能**であり、認可プロキシの調整は不要。
  スキーマ実測で判明した差異（未存在ページは GraphQL errors 6003 を返す・update は全項目必須・
  update の isPrivate 無視・ja ロケール要インストール）は実装へ反映済み。
  詳細: [20260707_wikijs-poc-record](../../docs/tech/20260707_wikijs-poc-record.md)。
- 削除・アーカイブの同期経路は [IADR-0023](./IADR-0023_document-delete-archive-wikijs-propagation.md) で実装した。

## 関連

- Supersedes: なし
- Superseded by: なし
- 作業仕様書: [20260705_ADR-0011-wiki-js-deployment](../specs/20260705_ADR-0011-wiki-js-deployment.md)
- 参照 IADR: [IADR-0020](./IADR-0020_wiki-js-deployment-abac-gateway.md)
