---
title: IADR-0019 データソースが原本へ既定 ABAC 属性を付与する（機密区分の発生源）
type: impl-adr
status: Accepted
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
  - ../specs/20260705_FR-01_datasource-default-attributes.md
related_adrs:
  - IADR-0000 (実装判断を記録する)
  - IADR-0004 (ABAC の多値 allow-list・deny-by-default)
  - IADR-0012 (Retrieval /search の fail-closed で ABAC を強制する)
---

# IADR-0019: データソースが原本へ既定 ABAC 属性を付与する

- 状態: Accepted
- 日付: 2026-07-05
- 決定者: claude（実装）
- 関連: FR-01、FR-05、UC-04、ADR-0004、Issue #64（親 #48）

## コンテキストと課題

DataSourceService の同期トリガーは原本取得イベント `RawDocumentFetched` を **`Attributes: []`（空）** で
発行していた。この属性は取り込みパイプラインを通じて文書チャンクの ABAC 属性（`confidentiality` 等）として
保持される唯一の発生源であり、空のまま流れると次の破綻が生じる。

- 文書に機密区分（`confidentiality`）が付与されない。
- `RetrievalService /search` は fail-closed（IADR-0012）で ABAC を強制するため、機密区分を持たない文書は
  利用者の許可条件と突合できず**検索結果から除外**される。
- 結果、パイプライン経由で取り込んだ文書が**実配備で検索に一切ヒットしない**（FR-01/FR-05 の前提が欠落）。

## 決定

**データソースを ABAC 文書属性の発生源とする。** データソースは登録時に既定属性 `DefaultAttributes`
（`Dictionary<string,string>`）を持ち、同期で発行する各 `RawDocumentFetched.Attributes` へ写像する。

### 1. 機密区分の既定値は `internal`（フェイルセーフ）

`confidentiality` が未指定・空文字の場合、既定値 `internal` を補完する。

- 許可値は AuthorizationService の属性辞書に準拠（`public / internal / confidential / restricted`）。
- **`public`（過剰公開）でも `restricted`（過剰制限）でもなく `internal`** を採る。社内データソース由来の
  文書は既定で「社内限」とみなすのが最も安全側かつ実用的な基準であり、機密区分の付け忘れによる
  fail-closed での全消失を防ぎつつ、公開扱いによる過剰露出も避ける。
- 明示指定された属性（機密区分・部門など）はそのまま尊重し、既定値で上書きしない。

### 2. フェイルセーフは「発行時」に一元化する（既存行の回帰防止）

補完ロジックを 2 箇所に置く。`DataSource.Create`（新規登録時）と、**原本発行時に必ず通る
`DataSource.GetEffectiveAttributes()`** である。`/{id}/sync` は `DefaultAttributes` を直接コピーせず、
必ず `GetEffectiveAttributes()` を経由して `RawDocumentFetched.Attributes` を組み立てる。

- これにより、**本 IADR のマージ前から登録済みで `confidentiality` を持たない既存データソース**でも、
  同期時に `internal` が確実に補完され、fail-closed 除外（IADR-0012）を再発させない。
- 補完ロジックは `DataSource` 内の単一のプライベートヘルパに集約し、`Create` と
  `GetEffectiveAttributes` の挙動が乖離しないようにする。

### 3. 永続化は既存 `Config` と同一方式・既存行はマイグレーションで backfill

`DefaultAttributes` は jsonb カラムとして保管する（`Config` と同じ JSON 変換・ValueComparer）。
既存行にはマイグレーションで **`{"confidentiality":"internal"}`** を既定値として付与し、永続表現も
発行時のフェイルセーフと整合させる（空 `{}` ではない）。発行時の `GetEffectiveAttributes()` が
最終防衛線であり、backfill 値に依存せず必ず機密区分を保証する。

## 検討した選択肢

- **A. 既定値を `restricted`（最厳格）にする**: 情報漏洩の観点では最も安全だが、大半の社内文書が
  管理者の明示付与まで検索不能になり、実運用が成立しない。**不採用**。
- **B. 既定値を `public` にする**: 検索には出るが、機密文書を公開扱いする過剰露出リスク。fail-closed の
  設計思想に反する。**不採用**。
- **C. データソース単位の既定属性 ＋ 未指定は `internal`（本決定）**: 付け忘れによる全消失を防ぎつつ、
  データソース登録時に機密区分を宣言できる。ABAC の発生源をパイプライン入口へ一本化できる。**採用**。

## 結果

- 良い影響: 取り込んだ文書が機密区分を必ず持ち、fail-closed 検索から除外されなくなる。ABAC 属性の
  発生源がデータソース登録に一本化され、トレーサビリティが明確になる。
- トレードオフ: `internal` 既定はあくまでフェイルセーフであり、正確な区分付けは登録時の属性宣言に依存する。
  文書ごとの区分上書きは DocumentService 側の属性編集（FR-06/FR-09）に委ねる。
- スコープ外（フォローアップ）: 登録時の AuthorizationService `/attributes/validate` との整合検証、
  実ファイル取得コネクタ、ソース種別ごとの区分自動判定は本 IADR では扱わない。
