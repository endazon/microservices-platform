---
title: IADR-0012 Retrieval /search は Scope 未指定を deny 扱いにし fail-closed で ABAC を強制する
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - UC-01
  - ADR-0004
  - ADR-0005
author: claude
created: 2026-07-03
updated: 2026-07-03
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-05)
  - planning:projects/microservices-platform/07_adr/ (ADR-0004 Keycloak + ABAC, ADR-0005 Istio mTLS)
supersedes: なし
superseded_by: なし
---

# IADR-0012: Retrieval /search は Scope 未指定を deny 扱いにし fail-closed で ABAC を強制する

- 状態: Accepted
- 日付: 2026-07-03
- 決定者: claude（実装）
- 関連: ADR-0004（Keycloak + ABAC）、ADR-0005（Istio mTLS）、IADR-0004（ABAC 多値 allow-list ＋ deny-by-default）
- 親監査: #48（`adr-guardian` 横断監査）、起票: #55

## コンテキストと課題

#48 の横断監査で、`RetrievalService` の `/search` に **ADR-0004 / IADR-0004 違反（重大）** を検出した。

1. `HybridSearchService.SearchAsync` は `request.Scope is { GrantsAccess: false }` の時のみ短絡的に空を返す。
   このため **`Scope == null`（未指定）はスコープフィルタ無しで全文書を返す**。呼び出し側が渡す
   `AccessScope` を無検証で信任しており、Scope を付けない呼び出しが ABAC を全面バイパスできる。
2. `/search` エンドポイントは `RequireAuthorization` を持たず、認証必須になっていない。
3. `deploy/docker-compose.yml` で 5003 番ポートがホスト公開されており、Istio mTLS（ADR-0005）未実装の
   現状では、ネットワーク到達可能な相手が上記 1・2 を突いて権限外文書を取得できる。

IADR-0004 は deny-by-default を「二重強制（RagOrchestrator 一次／HybridSearchService 二次）」で担保する設計
だが、二次強制点が `GrantsAccess=false` のみを見て `null` を素通ししていたため、多重防御が成立していなかった。

## 検討した選択肢

1. **`HybridSearchService` で `Scope` が「`GrantsAccess=true` の明示的許可」でない限り空を返す（fail-closed）。**
   `null` と `GrantsAccess=false` を同義（閲覧可能文書なし）に扱う。小規模・即時対応。
2. `/search` を `RequireAuthorization` で認証必須化し、JWT から利用者を特定して RetrievalService 自身が
   `AuthorizationService /authz/scope` を呼んで Scope を解決する（呼び出し側の Scope を信任しない）。
3. 1 と 2 の両方を同時に実施する。

## 決定

**選択肢 1 を即時採用する。選択肢 2（エンドポイント認証／自己スコープ解決）は本 IADR に判断を記録した上で
後続タスクへ分離する。**

- `HybridSearchService.SearchAsync` の deny 判定を
  `request.Scope is { GrantsAccess: false }` から `request.Scope is not { GrantsAccess: true }` へ変更する。
  → `Scope == null`（未指定）も `GrantsAccess=false` も等価に「何も返さない」へ倒す。
  → `GrantsAccess=true` の明示的許可がある時だけ検索を実行する（positive grant 必須）。
- 受け入れ基準「Scope 未指定 → 結果 0 件」を単体・結合テストへ写像する。

## 理由

- **fail-closed が deny-by-default の本義**: 「未解決＝許可されていない」と扱わなければ、認可情報の欠落が
  そのまま全件開放になる。二次強制点（HybridSearchService）を positive grant 必須にすることで、
  一次強制点（RagOrchestrator）を経由しない直接呼び出しでも権限外文書が出ない。
- **本番経路を壊さない**: 唯一の本番呼び出し元 `RagOrchestrator.GenerateAsync` は常に
  `AccessScope{ Filters, GrantsAccess }` を明示的に付与して `/search` を呼ぶ（`RagOrchestrator.cs`）。
  未許可時は `GrantsAccess=false`。よって `null` を deny 化しても正規経路の挙動は不変。
- **単値 `AttributeFilters`（FR-03 後方互換）は認可境界ではない**: これは呼び出し側が任意に付ける
  クエリフィルタであり、権限の根拠にならない。したがって許可スコープ（`GrantsAccess=true`）が無い限り、
  `AttributeFilters` だけの呼び出しも deny する。認可境界は一貫して `AccessScope` に集約する。

### 選択肢 2 を今回実装しない理由（トレードオフ）

- `/search` の `RequireAuthorization` 化には (a) 内部サービス間呼び出し（AiAnalysisService → RetrievalService）
  への JWT 伝播、(b) 結合テスト基盤（`TestWebApplicationFactory`）へのテスト認証ハンドラ整備が必要で、
  影響範囲が広い。今回の重大脆弱性の即時封じ込め（fail-closed）とは分離するのが安全。
- RetrievalService 自身での Scope 自己解決は、責務配置（現状は AiAnalysisService の `RagOrchestrator` が
  `/authz/scope` を解決）に関わる設計変更であり、単独 IADR で扱うべき規模。
- ネットワーク到達性の根治は ADR-0005（Istio mTLS）で担保される想定。それまでの緩和として、
  5003 番ポートのホスト公開見直し（内部ネットワーク限定）を運用側の後続タスクとする。

## 結果

- 良い影響: FR-05 受け入れ基準「権限の無い文書は検索結果・AI 回答のいずれにも一切現れない」の
  抜け穴（Scope 未指定→全件）を塞ぐ。IADR-0004 の「二次強制点」が設計どおり機能する。変更は
  RetrievalService に閉じ、契約変更なし（個別デプロイ・ロールバック可能）。
- 悪い影響・トレードオフ: `/search` を直接叩く新規呼び出し元は、必ず `GrantsAccess=true` の Scope を
  付与する必要がある（Scope 無しは 0 件）。後方互換の `AttributeFilters` 単独呼び出しは deny される。
- フォローアップ（後続タスクへ分離）:
  - `/search` の認証必須化 or RetrievalService による Scope 自己解決（選択肢 2）。
  - `deploy/docker-compose.yml` の 5003 番ポート公開の見直し（ADR-0005 mTLS 導入まで内部限定）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 補強する決定: [IADR-0004](./IADR-0004_abac-multivalue-allowlist-deny-by-default.md)（deny-by-default 二重強制）
- 作業仕様書: [20260703_fix_FR-05-search-scope-fail-closed](../specs/20260703_fix_FR-05-search-scope-fail-closed.md)
