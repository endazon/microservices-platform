---
title: 検索結果一覧 画面仕様書
type: screen-spec
status: draft
related_ids:
  - SC-02
  - UC-01
  - FR-03
  - FR-05
  - IADR-0121
  - IADR-0124
  - IADR-0125
  - IADR-0126
author: claude
created: 2026-07-09
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
related_specs:
  - "./SC-01_search-chat.md"
  - "./SC-03_document-detail.md"
  - "../adr/IADR-0038_bff-document-read-abac-gating.md"
  - "../adr/IADR-0126_sse-answer-state-and-search-url-state.md"
  - "../adr/IADR-0121_spa-stack-migration-staging.md"
  - "../specs/20260804_issue-502_sc01-03-search-flow.md"
  - "../tests/SC-02_search-results.md"
---

# 画面仕様書: 検索結果一覧（SC-02）

> **［2026-08-04 / #502］新スタックでの再実装に合わせて全面改訂した。**
> ルート `/search?q=` は #490（[[IADR-0124]] 決定 6）で計画へ是正済みであり、本改訂でも変えていない。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-02 検索結果一覧**（[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-02）
- 関連ユースケース（UC）: **UC-01**（検索・質問する。**代替フロー**「キーワード検索のみで結果一覧を返し、AI回答を省略する」の受け皿）
- 関連機能要求（FR）: **FR-03**（ハイブリッド検索）・**FR-05**（ABAC）
- モックアップ（**実装の正**）: [hi-fi/sc-02.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-02.html) ／ [wireframe/sc-02.html](../../planning/projects/microservices-platform/05_screens/mockups/wireframe/sc-02.html)
- 関連 IADR: [[IADR-0126]]（検索条件を URL に置く）・[[IADR-0038]]（BFF 読み取りの ABAC ゲート）・[[IADR-0009]]（存在秘匿）

## 画面概要・目的

キーワード／意味（ハイブリッド）検索の結果を**権限内の文書に限って**一覧し、各件から SC-03（文書詳細）へ遷移する。
SC-01 が AI 回答を主とするのに対し、本画面は**一覧と内部導線**を担う。

- ルート: `/search?q=`（05_screens §共通シェル「ルートパス」）
- アクセス: 認証済みユーザー。ロール限定なし（`RequireAuth` のみ）。ABAC はサーバ側（BFF）で適用。

## hi-fi モックアップとの対応（実装する要素／実装しない要素）

行番号は planning `d980a01` の [hi-fi/sc-02.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-02.html) に対するものである。

| # | モックの要素（行） | 実装 | 備考 |
| --- | --- | --- | --- |
| 1 | 検索ボックス ＋「検索」（416） | **する** | `Input` ＋ `Button`。初期値は `?q=` |
| 2 | 「← チャットに戻る」（416 右） | **する** | 入力中の語を保って `/ask` へ |
| 3 | 「24件（権限内のみ表示）」（417 左） | **する** | `totalHits` ＋「（権限内のみ表示）」。**存在秘匿の説明そのもの**であり省略しない |
| 4 | 結果テーブル：**文書**（タイトル＋スニペット）（418-425） | **する** | `Table`。タイトルは `/docs/$id`（SC-03）への内部リンク |
| 5 | 結果テーブル：**タグ**（418-425） | **する** | `Tag`（分類）。0 件のときは空欄 |
| 6 | 結果 0 件（モックに無い状態） | **する** | 中立の文言。権限外と 0 件を区別しない（[[IADR-0009]]） |
| 7 | **並び順「並び: 関連度 ▾」**（417 右） | **しない** | **BFF に載せる先が無い**。§実装しない要素の理由 (a) |
| 8 | **検索モード切替「キーワード｜意味 ⇄」**（417 右） | **しない** | 同上 (b) |
| 9 | **結果テーブルの「更新」列**（419・421-423） | **しない** | 検索応答が更新日時を持たない。同上 (c) |
| 10 | 右レール「AIチャットパネル」（427-432） | **しない** | 共通シェルの要素。移行**第 4 段**（[[IADR-0121]] 決定 5） |
| 11 | パンくず（413） | **しない** | 共通シェルの要素。#452 |

### 実装しない要素の理由（**いずれも繰り延べであって放棄ではない**）

3 件はすべて同じ型の理由である——**画面の下にある契約（BFF ＋ 検索サービス）が当該の情報・操作を持たない**。
UI だけ置くと「押しても結果が変わらない操作」「常に空の列」を作ることになり、
計画が本画面へ与えた役割（権限内の結果を**正確に**見せる）をむしろ損なう。

| # | 計画の記述 | 現在の契約（実測） | 必要な変更 |
| --- | --- | --- | --- |
| (a) 並び順 | §SC-02 主要素「並び順（関連度ほか）」 | `SearchRequest(Query, TopK, AttributeFilters, Scope)` に並び順が無い。応答は関連度（`Score`）降順のみ | 検索 API への並び順パラメータ（＋「ほか」が何かの確定） |
| (b) 検索モード | §SC-02 主要素「検索モード切替（キーワード｜意味）」 | 同上。RetrievalService は**常にハイブリッド**（語彙＋ベクトル）で、片方だけに切り替える経路が無い | 検索 API へのモードパラメータ |
| (c) 更新日時 | §SC-02 主要素「結果テーブル（文書／タグ／**更新日時**、スニペット抜粋付き）」 | `SearchResultDto(ChunkId, DocumentId, DocumentTitle, Text, Score, MarkdownUri, Attributes, Tags)` に日時が無い | 検索結果 DTO への `UpdatedAt` 追加（索引への取り込みを含む） |

実測の出所: `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/SearchDto.cs` ／ `SearchResultDto.cs` ／
`src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/SearchBffEndpoints.cs`（対象コミット `83ff0fd`）。

**代替で埋めない判断**: (c) は `GET /bff/documents`（権限内の全文書）を引いて結合すれば表示できるが、
1 画面のために**カタログ全件**を取りに行くことになり、件数に比例して重くなる。
計画にない性能特性を実装の都合で持ち込まないため採らない。

3 件はまとめて計画へ環流する（[feedback/20260804_sc01-03-bff-contract-gaps.md](../../feedback/20260804_sc01-03-bff-contract-gaps.md)）。

## データソース（BFF 境界）

| 用途 | エンドポイント | 呼び出し方 | 認可 | 応答 |
| --- | --- | --- | --- | --- |
| 横断検索 | `POST /bff/search` | TanStack Query `useQuery` ＋ `apiFetch` | 認証・**サーバ側で ABAC スコープ解決** | `SearchResponse { results, totalHits, elapsedMs }` |

- 要求本文は `{ query, topK: 20 }` のみ。**クライアントは ABAC スコープを送らない**（送っても BFF は使わない＝権限昇格の防止）。
- キャッシュキーは `['bff', 'search', query]`。同じ語での再訪・戻る操作は `staleTime`（30 秒）内なら再要求しない。

## 検索条件の単一情報源（URL）

**検索語は URL（`?q=`）だけが持つ**（[[IADR-0126]] 決定 3）。

- 送信 → `navigate({ to: '/search', search: { q } })` → `?q=` が変わる → `useQuery` の key が変わる → 取得。
- 入力欄はローカル state（未確定の編集値）であり、**取得の引き金にはならない**。
- これにより、旧実装が持っていた「送信時の直接実行」と「`?q=` 変化の `useEffect`」の**二重発火**と、
  それを抑えるための `lastSearched` ガードが不要になる。
- 共有・ブックマーク・ブラウザの戻る／進むがそのまま再現される。

## レイアウト / 主要素

```text
┌──────────────────────────────────────────────────────────────┐
│ 検索結果一覧                                                   │
│ [経費精算                    ] [検索]   ← チャットに戻る        │
│ 24 件（権限内のみ表示）                                         │
├───────────────────────────────┬──────────────────────────────┤
│ 文書                            │ タグ                          │
├───────────────────────────────┼──────────────────────────────┤
│ 経費精算規程 v3.2               │ ［経理］［規程］               │
│ …精算の締め日は毎月25日とし…    │                              │
└───────────────────────────────┴──────────────────────────────┘
```

## 表示・入力項目

| 項目 | 種別 | 必須 | 形式・制約 | 説明 |
| --- | --- | --- | --- | --- |
| キーワード・意味検索 | `Input` | 必須 | 1 文字以上（前後の空白を除く）・最大 1000 文字 | 空では送信不可 |
| 件数 | 表示 | — | `{totalHits} 件（権限内のみ表示）` | 表示件数が総数より少ない場合は「（表示 N 件）」を添える |
| 文書 | 表示／リンク | — | タイトル ＋ スニペット | `/docs/$id`（SC-03）へ内部遷移 |
| タグ | 表示 | — | `Tag` の並び | 検索応答の `tags` |

## アクション・イベント

| 操作 | 挙動 | 遷移先 |
| --- | --- | --- |
| 検索 | `?q=` を更新する（取得は URL 変化に従う） | `/search?q=…` |
| ← チャットに戻る | 入力中の語を保って SC-01 へ | `/ask` |
| 結果クリック | 文書詳細へ | `/docs/$id` |

## 権限・表示条件・存在秘匿

- ABAC は BFF が deny-by-default で適用する。許可ポリシーが無ければ**空応答**である。
- 空一覧は「権限外」「該当なし」のどちらでも**同一の中立文言**を出す（[[IADR-0009]] / [[IADR-0038]]）。
  件数表示に「（権限内のみ表示）」を添えるのは、**この一覧が全体ではない**ことを利用者へ伝えるためであり、
  個々の文書の存在を示すものではない。

## エラー・状態

| 状態 | 表示 |
| --- | --- |
| `?q=` が空 | 何も取得せず検索ボックスのみ（`useQuery` は `enabled: false`） |
| 取得中 | 「検索中…」（`role="status"`） |
| 成功・0 件 | 「該当する文書が見つかりませんでした。」（中立） |
| 成功・1 件以上 | 件数 ＋ 表 |
| 失敗 | `Alert tone="danger"` `role="alert"`（`ApiError` の詳細があればそれを出す） |

## i18n

- 文言はすべて Lingui のカタログ（ja / en）へ載せる。`eslint-plugin-lingui` の適用範囲に本 feature を含める。

## UI 部品（`@platform/ui`）

`Input` / `Label` / `Button` / `Table` 一式 / `Alert` / `Tag`（新規。判定は [SC-01 §UI 部品](./SC-01_search-chat.md) に記載）。

## 関連仕様

- 作業仕様書: [20260804_issue-502_sc01-03-search-flow.md](../specs/20260804_issue-502_sc01-03-search-flow.md)
- テスト仕様書: [SC-02_search-results.md](../tests/SC-02_search-results.md)
- 計画への環流: [feedback/20260804_sc01-03-bff-contract-gaps.md](../../feedback/20260804_sc01-03-bff-contract-gaps.md)

## 未決事項

1. **並び順・検索モード・更新日時**（§実装しない要素）。BFF ／ 検索サービスの契約拡張が要る。環流済み。
2. **ページング**。計画は件数（24 件）を示すが送り方を定めていない。実装は `topK: 20` の 1 ページのみで、
   総数と表示件数の差を明示する。ページングの要否は計画の裁定を待つ。
