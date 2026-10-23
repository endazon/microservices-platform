---
title: 作業仕様書 — SC-05（一般の登録口 POST /documents）で作る組織文書に doc_scope=organization を作成時にサーバで補う
type: spec
status: done
related_ids: [FR-19, FR-06, UC-03, SC-05, ADR-0054, ADR-0058, IADR-0598, IADR-0278, IADR-0270, IADR-0283]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0054_doc-scope-attribute-for-private-note.md（決定 3・5・§結果）
  - planning:projects/microservices-platform/07_adr/ADR-0058_doc-scope-immutability.md（doc_scope は作成時に確定し以後変更不可）
issue: "#2112"
---

# 作業仕様書 — SC-05 で作る組織文書の doc_scope 補完（#2112）

> 本仕様書は実装着手前に作成した（着手 2026-10-23）。判断の記録は **IADR-0598**（新規）に置く。
> 親 issue #2047 の指摘 M-15。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-19**（個人資料。`doc_scope` 属性の導入元）・**FR-06**（文書の CRUD）。ユースケース: **UC-03**。画面: **SC-05**（文書管理の登録フォーム）。
- 計画 ADR（`origin/main` を読み取り専用で確認）:
  - **ADR-0054 決定 3**: `doc_scope` は必須。組織文書にも `organization` を置く。**決定 5**: システム投入経路の既定は `organization`。**§結果**: 既存文書へ遡及付与しない。
  - **ADR-0058**: `doc_scope` は作成時に確定し、以後変更できない。
- 引き継ぐ実装判断: IADR-0278（不変性。後からの新規付与・既存値の削除も「変更」として拒否）・IADR-0270 決定 2（値域検証。欠落は拒否しない）・IADR-0283（literal は `DocumentScopes`）。

## 現状（origin/develop `e51fcc74` で実測）

- SC-05 の `DocumentForm.tsx` は作成時に機密区分だけを送る（`doc_scope` の参照 0 件）。
- `POST /documents` の検証は「機密区分 → `doc_scope` の値域 → 個人資料経路の拒否（`private-note` は 400）」で、欠落は拒否も補完もしない。
- 更新口（`PUT /documents/{id}`・`PATCH …/metadata`）は `ValidateDocScopeUnchanged`（序数一致でなければ 400）で、後からの付与を拒む。
  → 人の経路で作った組織文書は `doc_scope` を持たないまま確定し、恒久的に欠落する。
- 他の作成経路は既に付けている: 個人資料（`/private-notes`・Obsidian 同期・競合解決）は `private-note`、DataSourceService の取り込みは `FillIfBlank(doc_scope, organization)`。**残る穴は一般の登録口だけ**である。

## 方針

- **サーバ側で補う**（`POST /documents` の検証を通った後、所有者の入れ直しと同じ位置で `doc_scope` キーが無ければ `organization`）。
  フロントだけ直すと MCP・機械クライアント（AST の KB 書き込み）・別画面で再発する。フロントは変えない。
- 明示の値は変えない（`organization` は通る。未知値・空文字・`private-note` は既存の検証で 400 —— 補完は検証の後なので上書きしない）。
- 更新口の不変性（IADR-0278）は変えない。遡及付与しない。

## 母集合（規則 9・10。着手前に引いた）

- 作成経路（`Document.Create` / `Document.CreateWithBody` の呼び出し元）: `Documents/Create`（本件）・`PrivateNotes/Create`・`ObsidianSync/Push`・`SyncConflicts/Resolve`（いずれも `private-note` を付与済み）。DataSourceService は HTTP で作成し既に `organization` を補う。
- 更新口で属性を全置換する呼び出し元: SC-05 の編集（`DocumentForm.tsx` が既存属性をスプレッド＝同値の同送で通る）。AST は本文の投入（`PUT /documents/{id}/body`）だけで属性を更新しない（`CatalogReportDraftKnowledgeCopy` は PATCH を使わないと明記）。
- 「補完で新たに誤りになる記述」（規則 10）: `docs/functional/FR-06`（作成の節）・`FR-19`（エラー表）に補完の行を足す。`DocumentAttributes.ValidateDocScope` の「欠落は拒否しない」は引き続き真（拒否はせず補う）。
- 試験の母集合: `POST` で `doc_scope` 無しの文書を作ってから `doc_scope` を同送せずに更新する試験 —— DocumentService.Tests で 18 件が赤（実測）、Knowledge.IntegrationTests で 3 クラス。いずれも SC-05 の形（同値の同送）へ直す。「`doc_scope` を持たない文書」を意図する 2 件（`DocScopeImmutabilityTests`）は DB 上で外して再現する。

## 受け入れ基準と試験の写像

| # | 基準 | 試験 |
| --- | --- | --- |
| 1 | SC-05 相当（`doc_scope` 未指定）の `POST /documents` で作った文書が `organization` を持つ（応答と保存の両方。本文あり・なし） | `DocScopeCreateDefaultTests.文書スコープ未指定の作成はorganizationを持つ`・`補完された文書はSC05の通常の保存で文書スコープを保つ` |
| 2 | 個人資料の作成経路では `organization` が付かない／一般口の `private-note` は 400 のまま | `個人資料の作成経路ではorganizationが付かない`・`組織文書の作成口に個人資料を指定すると従来どおり400` |
| 3 | 明示 `organization` は通る／不正値（未知・空文字・大小違い）は 400 | `明示のorganizationはそのまま通る`・`不正な文書スコープは補完されず従来どおり400` |
| 4 | 更新で後から付与できない（PUT・PATCH） | `DocScopeImmutabilityTests.文書スコープを持たない文書へ後から付与できない`・`メタデータ経路でも…`（新設） |

変異: 補完（`TryAdd`）を外すと基準 1 の 3 件と単体 1 件が赤（実測）。

## 限界・残余

- 補完前に作られた `doc_scope` を持たない文書は残る（ADR-0054 §結果のとおり遡及しない）。
- 属性を全置換する外部クライアントが `doc_scope` を同送しないと、補完後の文書の更新は 400 になる（IADR-0278 の既存規則。個人資料・取り込み文書では既にそうである）。IADR-0598 §結果。
