---
title: SC-01〜03 の画面要素のうち 5 点が現在の API 契約に載らない（対象範囲フィルタ・検索モード・並び順・更新日時・機密区分の表示名）
type: plan-feedback
status: open
category: UC/画面の差異
related_ids: [SC-01, SC-02, SC-03, UC-01, FR-03, FR-04, FR-05]
source_repo: microservices-platform
source_ref: "feat/SC-01-03-search-flow / docs/specs/20260804_issue-502_sc01-03-search-flow.md（#502）"
author: Claude（実装）
created: 2026-08-04
---

# フィードバック: SC-01〜03 の画面要素のうち 5 点が現在の API 契約に載らない

## 種別

UC/画面の差異（**計画は画面要素を定めているが、それを支えるバックエンド契約が存在しない**）。

## 起点となる計画書

- 機能要求（FR）: FR-03（ハイブリッド検索）・FR-04（根拠付き AI 回答）・FR-05（ABAC）
- ユースケース（UC）: UC-01（検索・質問する）
- 画面（SC）: **SC-01**（検索／チャット質問）・**SC-02**（検索結果一覧）・**SC-03**（文書詳細）
- 関連 ADR: ADR-0031（フロントエンドスタック）／実装側 [[IADR-0119]]（FR-17〜21 の着手保留。本件とは別論点）
- 計画書リンク: `projects/microservices-platform/05_screens/01_screens.md` §SC-01 / §SC-02 / §SC-03、
  `05_screens/mockups/hi-fi/sc-01.html` / `sc-02.html` / `sc-03.html`（planning `d980a01`）

## 現状（計画書の記述 / As-Is）

| # | 画面 | 計画の記述 | 現在の API 契約（実測） |
| --- | --- | --- | --- |
| 1 | SC-01 | §入力/バリデーション「**対象範囲フィルタ**｜任意｜選択｜**権限内のタグ／フォルダのみ選択可**」。hi-fi はチップ（タグ: 経理／フォルダ: /規程／＋絞り込み）で描く | `POST /bff/analysis/ask/stream` の要求は `AnalysisRequest(Question, Scope?)` のみ。**属性フィルタを取らない** |
| 2 | SC-01 | 同上（**候補は権限内のみ**） | 一般利用者が呼べる**タグ辞書・フォルダ一覧の BFF エンドポイントが無い**（タグ／属性の辞書は `/bff/admin/authz`＝管理者限定） |
| 3 | SC-02 | §主要素「**検索モード切替（キーワード｜意味）**」 | `SearchRequest(Query, TopK, AttributeFilters, Scope)` にモードが無い。RetrievalService は**常にハイブリッド**（語彙＋ベクトル）で、片方だけに切り替える経路が無い |
| 4 | SC-02 | §主要素「**並び順（関連度ほか）**」 | 同上。並び順パラメータが無く、応答は関連度（`Score`）降順のみ。**「ほか」が何かも計画に無い** |
| 5 | SC-02 | §主要素「結果テーブル（文書／タグ／**更新日時**、スニペット抜粋付き）」 | `SearchResultDto(ChunkId, DocumentId, DocumentTitle, Text, Score, MarkdownUri, Attributes, Tags)` に**日時が無い** |
| 6 | SC-03 | §主要素「属性・タグパネル（**機密区分**・部門・タグ）」。hi-fi は `internal` を「社内限」、SC-05 / SC-09 は `confidential` を「秘」と描く | 値集合は `public` / `internal` / `confidential` / `restricted` の **4 値**（06_technical/07_abac-attribute-model）。**モックに現れるのは 2 値の表示名だけ**で、`public` / `restricted` の表示名は計画のどこにも無い |

**実測の出所**（対象コミット `83ff0fd`・planning pin `d980a01`）:

- `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/SearchDto.cs`（`SearchRequest` / `SearchResponse`）
- `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/SearchResultDto.cs`（`SearchResultDto` / `CitationDto`）
- `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/{SearchBffEndpoints,AnalysisBffEndpoints}.cs`
- BFF のルートグループ全 10 件: `grep -rn 'MapGroup("/bff' src/platform/backend src/knowledge/backend`
  → `/bff/admin/authz`・`/bff/admin/config`・`/bff/analysis`・`/bff/conversion/jobs`・`/bff/dashboard`・
  `/bff/datasources`・`/bff/documents`（読み取り／書き込みの 2 グループ）・`/bff/feedback`・`/bff/search`
- 表示名の実測: `grep -n "社内限\|秘" planning/.../05_screens/mockups/hi-fi/sc-05.html sc-09.html`

## 問題点 / あるべき姿（To-Be）

**画面だけ作っても機能しない。** 1〜5 は「押しても結果が変わらない操作」「常に空の列」を生む。
6 は実装が表示名を決めると、それが**事実上の用語定義**になってしまう（機密区分は取り違えの影響が大きい）。

#502 ではいずれも**実装せず**、画面仕様書へ「実装しない要素と理由」として明記した
（`docs/screens/SC-01_search-chat.md` §hi-fi モックアップとの対応 ／ `SC-02_search-results.md` 同 ／
`SC-03_document-detail.md` §属性の表示）。**繰り延べであって放棄ではない。**

あるべき姿は次のいずれかである（計画側の裁定を仰ぐ）。

- (A) 契約を拡張する。FR-03 に「検索モード・並び順の指定」「結果に更新日時を含める」を、
  FR-04 に「対象範囲の指定」を要求として明示し、実装側で API を拡張する。
- (B) 画面要素を落とす。モックの当該要素を削り、計画本文からも外す。
- (C) 保留する。要求としては残しつつ、実装の着手条件（対応 API の実装）を明記する。

## 実装で判明した経緯

#502（SC-01〜03 の新スタックでの再実装）で、hi-fi モックアップの要素を 1 つずつ実装可能性へ写像する過程で判明した。
旧実装（削除済み）はこれらの要素を**そもそも作っていなかった**ため、差異が表面化していなかった。
再実装にあたり「モックを正として全要素を突き合わせる」作法を採ったことで顕在化した。

## 提案（計画への反映案）

- 反映先候補: **要求更新（FR-03 / FR-04）** ＋ **画面更新（SC-01 / SC-02）** ＋ **用語追加（機密区分の表示名）**
- 提案内容:
  1. **FR-03 へ検索の指定軸を明示する**（モード＝キーワード／意味／ハイブリッド、並び順＝関連度・更新日時ほか）。
     「ほか」を具体化しないと実装が推測することになる。
  2. **検索結果に更新日時を含める**ことを FR-03 の受け入れ基準へ加える（索引側の取り込みを伴う）。
  3. **FR-04（または SC-01）へ「対象範囲フィルタ」を支える 2 つの API を明示する**——
     (i) AI 回答要求への属性フィルタの付与、(ii) **権限内**のタグ／フォルダ候補の取得。
     とくに (ii) は「権限内のみ提示」という計画の保証を実現する唯一の手段であり、
     ABAC のスコープ解決結果を利用者へ返す API（存在秘匿との整合が要る）になる。
  4. **機密区分 4 値の表示名を用語集（`docs/glossary.md`）または 07_abac-attribute-model へ定める**
     （`public` / `internal` / `confidential` / `restricted`）。モックの「社内限」「秘」を正とするなら、
     残る 2 値の表示名も同じ場所に置く。
  5. 上記が確定するまでは、SC-01 / SC-02 の当該要素に「**対応 API 未実装のため画面も未実装**」の注記を置く。

## 影響範囲

- **実装**: RetrievalService（検索モード・並び順・更新日時）／ IngestionService（索引への更新日時の取り込み）／
  AuthorizationService ＋ BFF（権限内候補の取得 API）／ `Knowledge.Contracts`（DTO 変更。
  実装側の契約互換ゲート `scripts/check-contract-schema.js` の対象）。
- **画面**: SC-01（フィルタ）・SC-02（モード・並び順・更新列）・SC-08（AI 分析の範囲指定と同型の論点）。
- **存在秘匿**: 提案 3(ii) は「利用者が到達できる属性値の集合」を返すため、
  [ADR-0004](../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md) の 404 原則との整合が要る
  （文書の存在は示さないが、属性値の存在は示す）。**この点は ADR の判断事項である。**
