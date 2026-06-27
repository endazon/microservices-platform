---
title: IADR-0004 ABAC フィルタの多値 allow-list 化と deny-by-default
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - UC-01
  - UC-05
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-05)"
  - "../../planning/projects/microservices-platform/07_adr/ (ADR-0004)"
---

# IADR-0004: ABAC フィルタの多値 allow-list 化と deny-by-default

- 状態: Accepted
- 日付: 2026-06-27
- 決定者: claude（実装）
- 関連: ADR-0004（Keycloak + ABAC）、ADR-0009（Qdrant）、FR-03/FR-04 作業仕様書

## コンテキストと課題

FR-05 の中核は「権限の無い文書は検索結果・AI 回答のいずれにも一切現れない（deny-by-default）」である。
既存実装には ABAC の骨格があったが、以下の欠陥により本基準を満たしていなかった。

1. **多値 allow-list の欠落**: スコープ解決結果を検索フィルタへ変換する際、`AllowedValues.Count == 1` の
   フィルタのみ採用し、`confidentiality ∈ {public, internal}` のような多値条件を破棄していた。
   破棄された属性は無制約になり、機密文書が混入しうる。
2. **deny-by-default 不成立**: 利用者にマッチするポリシーが無い場合、解決フィルタが空になる。
   空フィルタを検索へ渡すと「全件返却」になり、本来「何も見せない」べき利用者に全文書が露出する。
3. **フィルタ契約の表現力不足**: 検索フィルタが `Dictionary<string,string>`（単値完全一致）で、
   ABAC の「key ∈ 許可値集合」を表現できなかった。

## 検討した選択肢

1. **検索の属性フィルタを多値 allow-list（`AttributeFilter` のリスト）へ統一し、スコープ解決に
   「許可有無（Granted）」を持たせて deny-by-default を明示的に判定する。**
2. 既存の単値 `Dictionary<string,string>` を維持し、多値はキーごとに複数リクエストへ分割して OR を取る。
3. 各文書 ID の可否を都度問い合わせる（per-document 認可）。

## 決定

選択肢 1 を採用する。

- 契約: `AccessScopeResponse` に `Granted`（利用者にマッチするポリシーが 1 つでもあったか）を追加。
  検索ワイヤ契約に `AccessScope{ Filters: AttributeFilter[], GrantsAccess }` を追加し `SearchRequest.Scope` に載せる。
- 評価: フィルタ間は AND、値集合内は OR。**スコープ対象の属性キーを持たない文書は除外**する。
- deny-by-default の強制点は二重化する。
  - 一次: `RagOrchestrator`（`Granted=false` なら検索・LLM を呼ばず空回答へ縮退＝コスト削減）。
  - 二次: `RetrievalService.HybridSearchService`（`GrantsAccess=false` で即時に空＝多重防御）。
- 既存の単値 `AttributeFilters`（FR-03）は `key → [単値]` と等価に正規化し、同一経路で評価する（後方互換）。
- Qdrant 実装は多値を `Match.Keywords`（いずれか一致）で表現し、キー間は `Must`（AND）で結合する。

## 理由

- 多値 allow-list は ABAC の意味論（属性が許可集合のいずれかに合致）を正確に表現し、条件破棄による
  漏えいを根絶する（選択肢 2 はリクエスト分割で複雑化し RRF 融合も乱れる）。
- `Granted` による明示的 deny-by-default は「ポリシー無し＝全件開放」という危険な既定を断つ。
- per-document 認可（選択肢 3）は p95 レイテンシ（NFR）に反するため不採用。インデックス側フィルタで
  候補段階から除外する方が高速かつ安全。
- 二重強制により、検索を直接叩く経路でも権限外文書が出ない（単一障害点を避ける）。

## 結果

- 良い影響: 受け入れ基準②（権限外文書を一切出さない）を満たす。契約変更は後方互換で、
  サービス個別デプロイ・ロールバック（基準④）を維持。
- 悪い影響・トレードオフ: スコープ対象属性を持たない既存文書は除外される（取り込み時に属性付与が前提）。
  属性未整備の文書は別途バックフィルが必要。
- フォローアップ: 利用者属性の正規ソース（Keycloak クレームマッピング）の確定、Qdrant `attributes.*`
  キーワードインデックスのブートストラップ、負荷試験による p95 実測は後続タスク。

## 関連

- Supersedes: なし
- Superseded by: なし
- 作業仕様書: [20260627_FR-05_abac-deny-by-default](../specs/20260627_FR-05_abac-deny-by-default.md)
