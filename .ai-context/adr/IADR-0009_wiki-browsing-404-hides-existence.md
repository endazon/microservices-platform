---
title: IADR-0009 Wiki 閲覧の権限外アクセスは 404 で存在を秘匿し、ABAC はメモリ内で後段評価する
type: impl-adr
status: Accepted
related_ids:
  - FR-13
  - FR-05
  - UC-07
author: claude
created: 2026-07-03
updated: 2026-07-03
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-13)
  - planning:projects/microservices-platform/03_usecases/01_usecases.md (UC-07)
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md
---

# IADR-0009: Wiki 閲覧の権限外アクセスは 404 で存在を秘匿し、ABAC はメモリ内で後段評価する

- 状態: Accepted
- 日付: 2026-07-03
- 決定者: claude（実装）
- 関連: ADR-0011（Wiki エンジン。ABAC は本システムがソースオブトゥルース、Wiki 側は表示制御）、
  ADR-0004（Keycloak + ABAC / deny-by-default）、[IADR-0004](./IADR-0004_abac-multivalue-allowlist-deny-by-default.md)（多値 allow-list・deny-by-default の評価意味論）

## コンテキストと課題

FR-13 / UC-07 は「管理している正規化文書を Wiki サービスで閲覧できる（ABAC・横断検索・AI回答と統合）」を求め、
UC-07 の例外フローは「権限の無い文書は一覧・本文のいずれにも表示しない」（受け入れ基準②）を要求する。
本 PR で WikiService の閲覧 API（一覧・個別 slug・個別 by-doc）に deny-by-default の ABAC を適用するにあたり、
既存の評価意味論（[IADR-0004]）を流用しつつ、本経路に固有の 2 点の実装判断が必要になった。

1. **個別ページの権限外アクセスに返すステータス**: 権限外の文書 slug / documentId を直接指定された場合に、
   403 Forbidden を返すと「その ID の文書は存在するが権限が無い」ことを応答から推測され、文書の**存在自体が漏えい**する。
2. **属性フィルタの評価場所**: `WikiPage.Attributes` は jsonb で保持されるため、ABAC 条件（key ∈ 許可値集合）を
   DB 側の SQL で表現するか、取得後にアプリのメモリ内で評価するかを決める必要がある。

## 検討した選択肢

1. 個別ページ: 権限外は **404 Not Found** に統一（不存在と同一応答）／ 属性フィルタは取得後の**メモリ内評価**。
2. 個別ページ: 権限外は 403 Forbidden を返す／ 属性フィルタは jsonb を SQL（`->>` 等）で DB 側評価。
3. per-document 認可（文書ごとに AuthorizationService へ都度問い合わせ）。

## 決定

選択肢 1 を採用する。

- **404 に統一（存在秘匿）**: `/wiki/pages/{slug}` と `/wiki/pages/by-doc/{documentId}` は、
  不存在・権限外のいずれの場合も `Results.NotFound()`（404）を返す。403 と 404 を区別せず、
  権限外文書の**存在有無を応答から区別できない**ようにする。一覧（`/wiki/pages`）は `Granted=false` を空配列、
  可視ページのみ返却とし、権限外は列挙に現れない。
- **メモリ内で後段評価**: DB から候補を取得したのち、純粋関数 `AbacPageFilter`（`Matches` / `Filter`）で
  `AccessScopeResponse` を適用する。評価意味論は検索側 `InMemoryVectorStore.MatchesFilters` /
  `AbacEvaluator` と一致させる（[IADR-0004]）: **フィルタ間は AND・値集合内は OR・属性キーを持たない文書は不一致・
  `Granted=false` は deny-by-default**。認可サービス障害時も 500 を伝播させず deny-by-default（`Granted=false`）へ縮退する
  （`RagOrchestrator.ResolveScopeAsync` と同一方針）。

## 理由

- **404 秘匿**: 403 は「対象が存在する」という事実を漏らし、ファイル名・documentId の総当りで機密文書の存在を
  マッピングされうる。UC-07 の「一覧・本文のいずれにも表示しない」を満たすには、存在自体を秘匿する 404 が適切。
  権限内ユーザーの正常系（200）には影響しない。
- **メモリ内評価**: ABAC の評価意味論を検索側と単一の実装（`AbacPageFilter`）で一致させ、経路間で
  可視性判定がずれる（＝一方で見えて他方で見えない）事故を防ぐ。jsonb を SQL 化すると意味論（多値 OR・
  属性欠落の扱い・大小無視比較）を DB 方言側で再実装することになり、二重管理・不整合のリスクが高い。
- per-document 認可（選択肢 3）は往復回数が増え p95（NFR）に反するため不採用（[IADR-0004] と同じ理由）。

## 結果

- 良い影響: 受け入れ基準②（権限外文書を一覧・本文いずれにも出さない）と UC-07 例外フローを満たす。
  評価ロジックが検索側と一致し、閲覧経路の秘匿性を保つ。
- 悪い影響・トレードオフ: 一覧 `GET /wiki/pages` は全件取得後にメモリ内で絞り込むため、ページ数増加に伴う
  取得コスト増がある（検索側と同方針の意図的トレードオフ）。ページング導入は後続課題。
- フォローアップ:
  - 一覧エンドポイントのページング／サーバ側絞り込み導入と、受け入れ基準⑤（p95）の負荷試験実測。
  - 計画側 ADR-0004 / ADR-0011 は現在 `Proposed`。Accepted への昇格を `/plan-feedback` でフォローする。

## 関連

- Supersedes: なし
- Superseded by: なし
- 作業仕様書: [20260703_FR-13_wiki-browsing-abac](../specs/20260703_FR-13_wiki-browsing-abac.md)
- 参照 IADR: [IADR-0004](./IADR-0004_abac-multivalue-allowlist-deny-by-default.md)
