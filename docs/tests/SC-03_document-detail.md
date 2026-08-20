---
title: SC-03 文書詳細／プレビュー テスト仕様書
type: test-spec
status: completed
created: 2026-07-09
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [FR-05, FR-06, FR-12, SC-03, UC-01, UC-02, UC-07]
adrs: [ADR-0031]
iadrs: [IADR-0119, IADR-0126]
specs: [01_screens, 01_usecases, 20260804_issue-502_sc01-03-search-flow, IADR-0038_bff-document-read-abac-gating, IADR-0119_fr17-21-hold-until-adr-fixed, SC-03_document-detail]
issues: []
-->

# テスト仕様書: SC-03 文書詳細／プレビュー

> **［2026-08-04 / #502］新スタックでの再実装に合わせて改訂した。**
> BFF（xUnit）のケースは #129 で作成済みであり本書に残す（本 issue では変更しない）。
>
> **［2026-08-05 / #510］§フロント・§純関数の見出しにテストファイル名を書き戻した。**
> 節そのものは残っていたが、#502 の改訂でファイル名（`DocumentDetailPage.test.tsx`）が落ちており、
> 本書からテストの実体へ辿れなくなっていた（SC-05 / SC-06 で起きた「節ごと落ちる」の軽い型）。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-03 ／ ユースケース（UC）: **UC-01**（出典・一覧からの到達）・**UC-02**（AI 分析の出典からの到達）・**UC-07**（Wiki 閲覧への導線）／ 機能要求（FR）: FR-05・FR-06・FR-12

## UC のフロー → テストの写像

| フロー | 画面での現れ方 | テスト |
| --- | --- | --- |
| **UC-01 基本 5**（出典付きで返す）／**UC-02 基本 4**（結果と出典を返す） | 出典・一覧から `/docs/{id}` を開くと、本文・属性・版履歴が出る | `renders title, markdown body, attributes and version history` |
| **UC-02 例外**（対象が権限外の場合は対象から除外する。**権限の有無は利用者に開示しない**） | 404 を「見つかりません」と中立に表示し、**5xx とは別**の表示にする | `shows a neutral not-found message on 404 (existence hidden)` |
| **UC-07 基本 1**（利用者が Wiki で文書を開く） | 「Wikiで閲覧」から SC-04 へ | `links to SC-04 only when a wiki base url is configured` |
| **UC-07 例外**（権限外の文書は一覧・本文のいずれにも表示しない） | 本画面は 404 で何も出さない（**UC-02 例外と同一の中立表示**） | `shows a neutral not-found message on 404 (existence hidden)` ＋ `never requests the version history when the document is hidden`（秘匿された文書へは追加の要求も出さない） |
| **[[IADR-0119]] 決定 1**（FR-17 / FR-18 の着手保留） | **AI 提案の承認欄と SC-18 への導線を描かない** | `does not render the AI suggestion panel or the knowledge-graph link (deferred features)` |

> **最後の行は「無いこと」を固定するテストである。** 保留対象を後から不用意に足すと落ちるため、
> 「保留の解除は [[IADR-0119]] 決定 6 の手順を踏む」という制約がテストとしても効く。

## フロント（Vitest + Testing Library）: `DocumentDetailPage.test.tsx`

| # | ケース | 期待 | 起点 |
| --- | --- | --- | --- |
| 1 | 正常 | タイトル・状態・版・本文（Markdown 原文）・属性・タグ・版履歴 | —|
| 2 | 属性ラベル | `confidentiality` → 「機密区分」、`department` → 「部門」、未知キーはそのまま。**値は変換しない** | 計画 §SC-03 主要素 |
| 3 | Wiki 導線 | `wikiBaseUrl` 設定時のみ `/wiki` へのリンク | —|
| 4 | 原本リンク | `http(s)` はリンク、`storage://` 等は等幅表記（リンクにしない） | 計画 §SC-03 主要素 |
| 5 | 404 | 中立「文書が見つかりませんでした。」 | **UC-02 例外** / [[IADR-0009]] |
| 6 | 5xx | `role="alert"`（404 とは別表示。サーバの状態であって文書の有無ではない） | — |
| 7 | 本文取得失敗 | 詳細は表示、本文領域のみ「本文は利用できません。」へ縮退 | — |
| 8 | 版履歴の取得抑止 | 詳細が 404 のとき、**版履歴を要求しない** | [[IADR-0126]] 決定 4 |
| 9 | 版履歴の失敗 | 版履歴パネルを出さず、本体表示は継続 | — |
| 10 | **保留対象の不在** | 「AI 提案」「知識グラフ」の語が画面に無い | **[[IADR-0119]] 決定 1** |
| 11 | ロケール `en` | 見出しが英語で描画される | —|

### 純関数（`attributes.ts` ／ `attributes.test.ts`）

| # | 入力 | 期待 |
| --- | --- | --- |
| P-1 | `confidentiality` | ラベル「機密区分」 |
| P-2 | `department` | ラベル「部門」 |
| P-3 | 未知のキー（`owner` 等） | キーをそのまま返す |

## BFF（xUnit・#129 で作成済み）: `BffDocumentEndpointTests`

| # | ケース | 期待 |
| --- | --- | --- |
| 1 | 許可 & 属性がスコープ内 → 詳細取得 | 200・`DocumentDto` |
| 2 | スコープ非許可（deny-by-default） → 詳細 | 404（存在秘匿） |
| 3 | 許可だが属性がフィルタ外 | 404（存在秘匿） |
| 4 | DocumentService が 404（不在） | 404（不在と拒否を区別しない） |
| 5 | 一覧: internal のみ許可 | 権限内 1 件のみ、secret 文書は非列挙 |
| 6 | 一覧: 非許可 | 空配列 |
| 7 | 本文: 許可（ストレージ未配備） | 200・プレースホルダ本文＋`sourceUri` |
| 8 | 本文: 非許可 | 404 |
| 9 | 版履歴: 許可 | 200・版一覧（新しい順） |
| 10 | 版履歴: 非許可 | 404 |

## E2E（Playwright）

| # | ケース | 期待 |
| --- | --- | --- |
| E-1 | 未認証で `/docs/<id>` | `/login` へ誘導（`?from=` 保持） |

## 手動確認（任意）

- 実 MinIO 配備時に `storage://` から実本文が取得されること（未配備時はプレースホルダ）。

<!-- trace-table:
row1: FR-06, FR-12
row2: UC-07
row3: ADR-0031
-->
