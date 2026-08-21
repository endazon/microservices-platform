---
title: 作業仕様書 — Wiki.js 同期・認可ゲートウェイ実装（IADR-0020 段2）
type: spec
status: in-progress
related_ids:
  - FR-13
  - UC-07
  - ADR-0011
  - IADR-0009
author: claude
created: 2026-07-05
updated: 2026-07-05
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-13)
  - planning:projects/microservices-platform/03_usecases/01_usecases.md (UC-07)
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md
related_specs:
  - ./20260705_ADR-0011-wiki-js-deployment.md
  - ./20260703_FR-13_wiki-browsing-abac.md
related_adrs:
  - ADR-0011 (閲覧基盤に Wiki.js 採用。ABAC は本システム側が真実源、Wiki 側は表示制御)
  - IADR-0009 (Wiki 閲覧の 404 存在秘匿・メモリ内 ABAC 評価)
  - IADR-0020 (Wiki.js 配備・WikiService を同期/ABAC ゲートウェイへ縮退。本 spec は段2 の実コード)
  - IADR-0021 (Wiki.js 同期方式は GraphQL API push)
---

# 作業仕様書: Wiki.js 同期・認可ゲートウェイ実装（Issue #66・IADR-0020 段2）

## 目的

段1（配備・OIDC 構成・意思決定記録）に続き、[IADR-0020] 段2 の**実コード**を実装する。
`WikiService` を自前閲覧実体から **Wiki.js への同期 ＋ 前段 ABAC 認可ゲートウェイ**へカットオーバーする。
受け入れ基準②（更新で Wiki.js に反映）・③（権限外は 404 で存在秘匿）・⑤（同期・統合へ縮退・自前閲覧実体撤去）
を新構成で満たす。

## スコープ（本 PR で実装）

1. **Wiki.js 同期（GraphQL push・[IADR-0021]）**
   - `IWikiJsClient` / `WikiJsGraphQlClient`: `pages.singleByPath` で既存を引き `pages.create` / `pages.update` を
     冪等呼び出し（path=`doc/<DocumentId>` の安定キー）。認証は Bearer（API キー）。失敗は例外送出 → MassTransit
     リトライ／デッドレター（`UseKnowledgePlatformRetry`）。
   - `IWikiContentReader` / `StorageMarkdownReader`: `MarkdownUri` から正規化 Markdown を取得（http(s) 実取得・
     dev はプレースホルダ。`IngestionService.StorageDocumentContentReader` と同方針）。
   - `DocumentSyncConsumer`: 自前 `wiki_svc` 書き込み → **本文取得 → Wiki.js push ＋ ABAC 同期メタデータ upsert**。
     認可属性（`Attributes`）は Wiki.js へ push しない（認可は本システムが単一真実源）。
   - **多層防御（AI レビュー対応）**: 機密区分由来の粗粒度な非公開設定 `isPrivate` を push に付与する。
     `confidentiality=public` 以外（属性欠落含む）は Wiki.js 上でも非公開（deny-closed）。ネットワーク分離
     （[IADR-0017]）が退行しても public 以外が無条件公開にならない第 3 の防御線（[IADR-0021]。ABAC の代替ではない）。

2. **認可ゲートウェイ（前段 ABAC・[IADR-0009] 継承）**
   - `WikiEndpoints` を Wiki.js 前段の認可プロキシへ改修。一覧は `AbacPageFilter` で権限内メタデータのみ。
     個別（slug / by-doc）は **ABAC 通過時のみ** `IWikiJsClient.GetRenderedContentAsync` で Wiki.js 本文を
     プロキシ取得。権限外・不存在・Wiki.js 未反映はいずれも **404**（存在秘匿）。
   - 自前 `wiki_svc` は**閲覧本文の実体提供を撤去**し、ABAC 判定用の同期メタデータ（属性/タグ/slug/status）に限定。
     `WikiPage.WikiPath`（`doc/<DocumentId>`）を同期 push と本文取得で共有する正準パスとする。

3. **配線・設定**
   - `Program.cs`: `IWikiJsClient` / `IWikiContentReader` を typed HttpClient で登録。`WikiJs:GraphQlEndpoint` /
     `WikiJs:ApiKey`（appsettings。compose の `WIKIJS_API_KEY`、Helm Secret `wikijs-sync`）。
   - Helm: 汎用 Deployment に `extraEnv`（value / secretKeyRef）を追加し、`wiki` サービスへ同期設定を注入。

4. **テスト（受け入れ基準の再充足）**
   - `DocumentSyncConsumerTests`: Wiki.js push（記録スタブ）・DocumentId 由来パス・未公開は非同期を検証。
   - `WikiEndpointsAbacTests`: 一覧=権限内のみ・個別=404（権限外/不存在）を維持し、200 時は Wiki.js 本文を
     プロキシすることを追加検証（slug・by-doc 双方で 200/404 を対称に検証。スタブ `IWikiJsClient`）。
     `AbacPageFilterTests` は不変で温存。
   - `DocumentSyncConsumerTests`: `confidentiality`→`isPrivate` 対応（public のみ公開）・属性欠落時の deny-closed も検証。

## 含まないもの（フォロー）

- 稼働 Wiki.js での GraphQL スキーマ整合・エラー時再送・レイテンシの **PoC 実測**（[IADR-0021]）。
  本実装は `IWikiJsClient` 背後にスキーマ結合を隔離し、差異吸収を容易にしている。
- Wiki.js 側 OIDC ローカルログイン無効化の**稼働検証**（手順は `docs/operations/operations.md`）。
- 検索側（Retrieval/AiAnalysis）との統合（受け入れ基準①）は別途担保済み。
- **文書の削除・アーカイブ（非公開化）に対する Wiki.js 同期経路**（実体撤去・メタデータ `Archived` 化）は
  既存の設計ギャップであり本 PR の範囲外。`isPrivate` 多層防御で public 以外は非公開だが、実体撤去は
  別途フォロー課題とする（[IADR-0021] フォローアップ・`docs/security/security.md` 未決事項）。
- 稼働 Wiki.js での `isPrivate=true` ページのサービスアカウント本文取得可否・ネットワーク分離の CI/E2E 検証。

## 受け入れ基準との対応

| # | 受け入れ基準 | 本 PR の対応 |
| --- | --- | --- |
| 2 | `DocumentUpdated` で Markdown が Wiki.js に反映 | `DocumentSyncConsumer` → `IWikiJsClient` GraphQL push（冪等 upsert）。稼働 PoC はフォロー |
| 3 | 権限外は一覧非表示・個別 404（存在秘匿） | `WikiEndpoints` 認可プロキシ（`AbacPageFilter` を到達可否へ転用）。テストで再充足 |
| 4 | Helm でも同等構成 | 汎用 Deployment の `extraEnv` で同期設定を注入（`wikijs-sync` Secret） |
| 5 | 同期・統合へ縮退・自前閲覧実体の撤去/非公開化 | 閲覧本文の実体提供を撤去し `wiki_svc` を同期メタデータに限定。本文は Wiki.js に委譲 |

## テスト観点

- deny-by-default：`Granted=false` で一覧空・個別 404。
- 存在秘匿：権限外 slug / by-doc は 404（403 で存在を漏らさない）。
- 同期：`DocumentUpdated` 受信で Wiki.js への push が発生し、DocumentId 由来の安定パスを用いる。未公開は同期しない。
- プロキシ：ABAC 通過時のみ Wiki.js 本文を返す（本文は自前 DB でなく Wiki.js 由来）。

## 検証

- 本サンドボックスは `dotnet` 実行がブロックされ、ビルド/テストを実走できない。コードは手作業で精査し、CI 検証に委ねる。
- 稼働 Wiki.js を要する GraphQL 実測は [IADR-0021] の PoC フォローとして残す。
