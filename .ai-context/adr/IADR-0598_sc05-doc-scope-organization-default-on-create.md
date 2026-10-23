---
title: IADR-0598 一般の登録口（POST /documents）で doc_scope が未指定なら organization をサーバで補う（作成時に確定させる。明示値・検証・更新口の不変性・遡及しない方針は変えない）
type: impl-adr
status: Accepted
related_ids: [FR-19, FR-06, UC-03, SC-05, ADR-0054, ADR-0058, IADR-0278, IADR-0270, IADR-0283, IADR-0398]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0054_doc-scope-attribute-for-private-note.md（決定 3・5・§結果）
  - planning:projects/microservices-platform/07_adr/ADR-0058_doc-scope-immutability.md
related_specs:
  - ../specs/20261023_2112_sc05-doc-scope-default.md
---

# IADR-0598: SC-05 で作る組織文書の doc_scope をサーバで補う（#2112）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2112。親 #2047 の指摘 M-15）

## 起点・関連

- 起点 issue: #2112（親 #2047）。
- 関連する計画書 ID: FR-19・FR-06・UC-03・SC-05。計画 ADR: ADR-0054（決定 3・5・§結果）・ADR-0058。
- 引き継ぐ実装判断: [IADR-0278](./IADR-0278_doc-scope-immutability-enforcement.md)（不変性）・IADR-0270 決定 2（値域検証。欠落は拒否しない）・IADR-0283（literal は `DocumentScopes`）・IADR-0398 決定 3（登録の属性規則は 413 の後ろ）。
- 作業仕様書: [20261023_2112_sc05-doc-scope-default](../specs/20261023_2112_sc05-doc-scope-default.md)。

## コンテキストと課題

- ADR-0054 決定 3 は `doc_scope` を必須とし、組織文書にも `organization` を置く。ADR-0058 は作成時に確定し以後変更できないとする。
- SC-05 の登録フォームは機密区分しか送らず、`POST /documents` は欠落を拒否も補完もしない。更新口は後からの付与を拒む（IADR-0278）。
  したがって**人の経路で作った組織文書は `doc_scope` を持たないまま確定し、恒久的に欠落する**。
- 他の作成経路は既に付けている（個人資料の 3 経路は `private-note`、DataSourceService の取り込みは `organization` を `FillIfBlank`）。残る穴は一般の登録口だけである。

## 検討した選択肢

1. **サーバ（`POST /documents`）で、検証を通った後に `doc_scope` キーが無ければ `organization` を補う（採用）**。クライアントを問わず閉じる（SC-05・MCP・AST の KB 書き込みなどの機械クライアント）。
2. **SC-05 のフォームが固定値 `organization` を送る**。人の画面は閉じるが、他のクライアントで再発する。単独では採らない。1 を採るとフロントの変更は要らないので入れない。
3. **`POST /documents` で欠落を 400 にする（必須化）**。ADR-0054 決定 3 の「必須」に字面では近いが、既存の呼び出し元（SC-05・AST の KB 書き込み）を一斉に壊し、両方の改修が揃うまで登録が止まる。決定 5 が取り込み経路に既定値を与えているのと同じく、既定値で埋めるほうが計画の意図に沿う。採らない。
4. **更新口で、要求に `doc_scope` キーが無ければ現在値を入れ直す（`owner` と同じ扱い）を併せて入れる**。補完した文書を `doc_scope` を同送せずに更新するクライアントが 400 にならなくなる。
   ただし IADR-0278 が「既存値の削除も変更として拒否する（黙って捨てない）」と決めた規則を変える。本件の射程（作成時の欠落）を越えるので採らない（§結果の残余）。

## 決定

**決定 1（補う位置）**: `CreateDocumentEndpoint` で、属性の検証（機密区分 → `doc_scope` の値域 → 個人資料経路の拒否）を通った後、所有者を入れ直すのと同じ位置で `DocumentAttributes.WithOrganizationScopeIfAbsent` を通す。
保存される属性（応答も）に `doc_scope=organization` が入る。

**決定 2（判定はキーの有無だけ）**: キーが有れば値に依らず触らない。空文字・未知値・大小違い・`private-note` は決定 1 の前の検証が 400 で返しているので、補完が値を上書きして通すことは無い。

**決定 3（変えないもの）**: 更新口の不変性（IADR-0278。後からの付与・削除・変更は 400、同値の同送は通る）、個人資料の作成経路、補完前の文書への遡及しない方針（ADR-0054 §結果）、フロント（SC-05 の編集は既存属性をスプレッドするので同値の同送になる）。

## 結果

- 良い: SC-05 を含む一般の登録口で作る文書は、クライアントが送り忘れても `doc_scope=organization` を持って確定する。ADR-0054 決定 3 の「必須」を新規文書について満たす。
- 悪い（受容）:
  - **補完前に作られた `doc_scope` を持たない文書は残る**（遡及しない）。
  - **属性を全置換する外部クライアントが `doc_scope` を同送しないと、補完後の文書の更新は 400 になる**。IADR-0278 の既存規則であり、個人資料・取り込み文書では既にそうである。
    基盤内の呼び出し元（SC-05）は同送する。AST は属性を更新しない（本文の投入だけ）。試験のうち、`doc_scope` 無しで作って同送せずに更新していたもの（単体 18 件・結合 3 クラス）は SC-05 の形へ直した。
    この形の呼び出し元が問題になれば、選択肢 4 を IADR-0278 の改定として別に起こす。
