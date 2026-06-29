---
title: IADR-0005 指定データ範囲は ABAC スコープと交差させ権限を広げない（narrowing-only）
type: impl-adr
status: Accepted
related_ids:
  - FR-07
  - FR-05
  - UC-02
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-07)"
  - "../../planning/projects/microservices-platform/07_adr/ (ADR-0010)"
---

# IADR-0005: 指定データ範囲は ABAC スコープと交差させ権限を広げない（narrowing-only）

- 状態: Accepted
- 日付: 2026-06-27
- 決定者: claude（実装）
- 関連: FR-07（指定データ範囲での分析・比較・抽出）、FR-05/IADR-0004（ABAC 多値 allow-list + deny-by-default）、ADR-0010

## コンテキストと課題

FR-07 は利用者が「**指定データ範囲**」で分析・比較・抽出を依頼する機能である。
一方 FR-07 の受け入れ基準②は「**権限の無い文書は検索結果・AI 回答のいずれにも一切現れない**」を要求する。

ここで危険なのは、利用者が指定したデータ範囲（属性フィルタ）を検索フィルタへ素朴に合成すると、
**権限を広げてしまう**可能性があることだ。既存の検索フィルタ合成（`HybridSearchService.BuildFilters`）は
単値フィルタと ABAC スコープを**同一キーで OR 結合**する（FR-03 後方互換の意図）。
これは「利用者の希望条件を足す」用途では妥当だが、FR-07 のデータ範囲をそのまま載せると、
利用者が ABAC の許可値以外を指定した場合に許可集合が**広がり**、権限外文書が露出しうる。

## 検討した選択肢

1. **データ範囲を ABAC 許可スコープと AND で交差（intersection）させ、実効スコープが常に
   ABAC の部分集合になるよう専用ロジックで導出する（narrowing-only）。**
2. データ範囲を `SearchRequest.AttributeFilters`（単値）へ載せ、既存の合成（OR）に委ねる。
3. データ範囲を検索後の後置フィルタ（post-filter）として結果から除外する。

## 決定

選択肢 1 を採用する。`AiAnalysisService` に純粋ロジック `DataRangeScopeResolver.Resolve(abac, range)` を置き、
以下の意味論で**実効アクセススコープ**を導出する。

- ABAC が `Granted=false` なら、いかなる範囲指定でも `GrantsAccess=false`（deny-by-default を継承）。
- キーごとに：
  - ABAC と範囲が**同じキー**を制約 → 値集合の**積**（`A ∩ U`）。積が空なら**全体を deny**（安全側）。
  - ABAC のみが制約するキー → ABAC の値集合を維持。
  - 範囲のみが制約するキー → 追加（ABAC は当該キーを無制約に許可していたため、絞るのは安全）。
- 評価意味論は retrieval と一致（フィルタ間 AND、値集合内 OR）。値比較は大文字小文字非依存。

導出した実効スコープは `SearchRequest.Scope`（`AccessScope`）として渡し、検索側の deny-by-default
（`GrantsAccess=false` で即空）と二重防御を保つ。

## 理由

- **不変条件「実効スコープ ⊆ ABAC 許可スコープ」を構造的に保証**でき、データ範囲がどう与えられても
  権限を広げない。受け入れ基準②を満たす。
- 選択肢 2（OR 合成）は権限を広げうるため不採用（FR-07 の安全要件と矛盾）。
- 選択肢 3（後置フィルタ）はインデックス側で候補から除外できず、p95（NFR）と漏えい防止の両面で劣る。
- 範囲が権限外を指す場合に「ゼロ件」と「アクセス拒否」を区別せず空回答に統一し、範囲の存在自体を露呈しない。

## 結果

- 良い影響: データ範囲指定があっても権限外文書が一切出ない。FR-04 と検索・出典・LLM 経路を共通化でき重複が無い。
- トレードオフ: 範囲が権限外を含むと結果が空になり、利用者には「権限が無い」と「該当が無い」の区別が付かない
  （情報漏えい防止のため意図的）。将来 UI で「権限内に絞り込みました」等の非露呈な通知を検討。
- フォローアップ: タグ（`Tags`）による範囲指定（現状 retrieval フィルタは `Attributes` のみ対象）、
  負荷試験による p95 実測。

## 関連

- Supersedes: なし
- Superseded by: なし
- 作業仕様書: [20260627_FR-07_data-range-analysis](../specs/20260627_FR-07_data-range-analysis.md)
- 基盤 ADR: [IADR-0004](./IADR-0004_abac-multivalue-allowlist-deny-by-default.md)
