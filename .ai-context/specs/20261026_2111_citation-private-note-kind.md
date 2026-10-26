---
title: 作業仕様書 — 出典（CitationDto）に文書スコープ docScope を足し、SC-01 と右レールの出典行で個人資料を組織文書と区別して描く（#2111）
type: spec
status: done
related_ids: [FR-04, FR-19, FR-21, SC-01, ADR-0054, IADR-0119, IADR-0142, IADR-0122]
author: claude
created: 2026-10-26
updated: 2026-10-26
issue: "#2111"
---

# 作業仕様書 — 出典の個人資料の印（#2111）

> 本仕様書は実装着手前に作成した（着手 2026-10-26）。親は #2047（指摘 M-13）。

## 現状（origin/develop `27c40494` で再確認）

- **機密区分の表示は既に入っている**（#2017 / E-12）。`CitationList` が `confidentialityLabel`（#2018。写像は `lib/i18n/confidentiality.ts` の 1 か所）で表示名を出す。
  issue の現状欄（`d8b97e98` 時点）より後に入った。姉妹 issue #2110（M-14）も同じく #2018 / #2019 で解消済みである（4 画面と出典が同じ写像を使い、試験もある）。
- **残るのは個人資料の印である。** 契約 `CitationDto` に文書スコープ（`doc_scope`）が無く、`citationKind()` は `personal` を返さない。
  語彙（`CitationKind.personal`・アイコン `UserRound`・ラベル「個人資料」）は `CitationList` に用意済みで、使われていない。
- 右レール（`AiChatRail`）は `kindOf` を渡さず、すべて「組織文書」と描く。
- 着手保留（IADR-0119 決定 1）は FR-21 について 2026-08-22 に解除され（同 IADR の追補・#447）、FR-19 は IADR-0142 が置き換えている。
  SC-01 画面仕様書の「しない（着手保留）」は古い。

## 方針

- 契約: `CitationDto` の末尾に `string DocScope = DocumentScopes.Organization` を足す（既定値つき＝非破壊。IADR-0122 決定 2）。
  値は ABAC 文書属性 `doc_scope` の 2 値（`organization` / `private-note`。ADR-0054。`DocumentScopes`）。
  - 発行側（`CitationMapper`）は文書属性から読む。`private-note` だけを `private-note` とし、欠落・それ以外は `organization`（`DocumentScopes.IsPrivateNote` と同じ規則。キー欠落は個人資料ではない）。
  - openapi は `required` に入れ、`enum` にしない（`confidentiality` と同じ作法）。orval を再生成する。
- 画面: `citationKind(documentId, wikiIds, docScope)` は `docScope === 'private-note'` なら `personal` を返す（Wiki の台帳より先に見る。個人資料は Wiki へ同期されない。ADR-0046）。
  `CitationList` は `kindOf` が無いとき（右レール）も `docScope` で `personal` を判定する。
  - ラベルは「個人資料」。モックの「（自分のみ）」は所有者にだけ付すと画面仕様書が定めるが、出典は所有者を持たないので付けない（事実と異なる文言を出さない）。
- 「個人資料を含める」トグル（モック #10）は本件の射程外（検索の入力側の要素）。

## 受け入れ基準

- [x] 後段: 文書属性 `doc_scope=private-note` の検索結果は `DocScope = "private-note"`、欠落・`organization`・未知値は `organization`。修正前はコンパイルが通らない（欄が無い）
- [x] 契約: 既定値つきで、旧発行者の応答（欄なし）は `organization` として読める
- [x] 画面: SC-01 の出典行で、個人資料は「個人資料」のラベルと人のアイコン、組織文書は「組織文書」。Wiki の台帳に載っていても個人資料が勝つ
- [x] 画面: 右レールの出典行でも個人資料が区別される。修正前は「組織文書」で赤
- [x] 画面仕様書 SC-01（モック #11）・テスト仕様書・通信仕様書を改める

## 母集合（規則 9）

`git grep -n "CitationDto\|AskCitation\|citationKind\|kindOf"`（submodule を除く）で引いた。

| 当たり | 扱い | 理由 |
| --- | --- | --- |
| `Knowledge.Contracts/Dtos/SearchResultDto.cs` の `CitationDto` | **直す** | 契約 |
| `AiAnalysisService/Domain/CitationMapper.cs` | **直す** | 発行 |
| `RagOrchestrator.cs`・`IRagOrchestrator.cs`・`GraphExpandingSearchService.cs`・`IVectorStore.cs` | 外す | 型を運ぶだけ・コメントだけ |
| `docs/api/openapi.yaml` の `CitationDto` と生成物 `bff.schemas.ts` | **直す** | 契約の写し |
| `components/ai-chat/citations.ts`・`CitationList.tsx`・`sc01-search/components/SearchChatPage.tsx` | **直す** | 判定と描画 |
| 試験のフィクスチャ（出典を作る 型付きの 3 か所（`CitationList.test.tsx`・E2E の `sc01-search` / `sc08-analysis`）。型の無い 5 か所は既定で組織文書として読む） | **直す** | 必須の欄が増える |
| `scripts/contract-schema-baseline.json` | **直す**（`--update`） | 契約の検査器が既定値つきのメンバーの追加（非破壊）を検出する |
| AST（submodule） | 外す | 既定値つきの追加で非破壊。AST は出典の欄を読まない（`confidentiality` も読んでいない） |

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。
