---
title: 検索／チャット質問画面 画面仕様書
type: screen-spec
status: draft
related_ids:
  - SC-01
  - UC-01
  - FR-03
  - FR-04
  - FR-05
  - FR-08
  - IADR-0119
  - IADR-0121
  - IADR-0124
  - IADR-0125
  - IADR-0126
author: claude
created: 2026-07-08
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - "../adr/IADR-0037_llm-sse-streaming.md"
  - "../adr/IADR-0126_sse-answer-state-and-search-url-state.md"
  - "../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md"
  - "../adr/IADR-0121_spa-stack-migration-staging.md"
  - "../adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md"
  - "../specs/20260804_issue-502_sc01-03-search-flow.md"
  - "../tests/SC-01_search-chat.md"
  - "./SC-02_search-results.md"
  - "./SC-03_document-detail.md"
---

# 画面仕様書: 検索／チャット質問画面（SC-01）

> **［2026-08-04 / #502］新スタックでの再実装に合わせて全面改訂した。**
> 旧スタック（素の DOM ＋ 手書き state）の記述を、ADR-0031 のスタック
> （TanStack Router / TanStack Query / `@platform/ui` / Lingui）前提へ書き換えている。
> ルート `/ask` は #490（[[IADR-0124]] 決定 6）で計画へ是正済みであり、本改訂でも変えていない。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-01 検索／チャット質問画面**（[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-01。**本システムの主入口**）
- 関連ユースケース（UC）: **UC-01**（検索・質問する）
- 関連機能要求（FR）: **FR-03**（ハイブリッド検索）・**FR-04**（根拠付き AI 回答）・**FR-05**（ABAC）・**FR-08**（フィードバック）
- モックアップ（**実装の正**）: [hi-fi/sc-01.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-01.html) ／ [wireframe/sc-01.html](../../planning/projects/microservices-platform/05_screens/mockups/wireframe/sc-01.html)
- 関連 IADR: [[IADR-0037]]（SSE ストリーミング）・[[IADR-0126]]（SSE の状態管理と URL 状態）・[[IADR-0119]]（**FR-17〜21 の着手保留**）・[[IADR-0121]] / [[IADR-0124]] / [[IADR-0125]]（新スタック）・[[IADR-0009]]（存在秘匿）

## 画面概要・目的

1 つの入力から**根拠付き AI 回答**（真の SSE ストリーミング・出典併記）を得る、本システムの主入口。
キーワード検索だけが欲しい場合は同じ入力のまま SC-02（検索結果一覧）へ渡す（UC-01 代替フロー）。

- ルート: `/ask`（05_screens §共通シェル「ルートパス」）
- アクセス: 認証済みユーザー。**ロール限定なし**（`RequireAuth` のみ）。ABAC は後段（BFF／検索／AI）が
  narrowing・deny-by-default で適用し、UI は権限の有無を開示しない。

## hi-fi モックアップとの対応（実装する要素／実装しない要素）

**「モックに描かれているのに実装しない」箇所は、後から「作り忘れ」と誤解されないよう本表で名指しする。**
行番号は planning `d980a01` の [hi-fi/sc-01.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-01.html) に対するものである。

| # | モックの要素（行） | 実装 | 備考 |
| --- | --- | --- | --- |
| 1 | 見出し「ナレッジ検索・AI質問」＋副題（416-417） | **する** | `<h1>` ＋説明文 |
| 2 | 質問／キーワード入力＋「送信」（418） | **する** | `Input` ＋ `Button`。空文字は送信不可（計画 §入力/バリデーション「1文字以上」） |
| 3 | 「キーワード検索のみ →」（419 右端） | **する** | 入力中の語を保ったまま `/search?q=` へ（UC-01 **代替フロー**の実体） |
| 4 | AI回答（ストリーミング）パネル（420-424） | **する** | `Card`。token を逐次連結して表示 |
| 5 | 👍 / 👎 ＋「フィードバック」（423） | **する** | `done` で `answerId` を得てから有効化（FR-08） |
| 6 | 出典パネル（425-434） | **する** | `Card`。`📄`＋タグ「組織文書」／`📖`＋タグ「組織文書」（Wiki 由来） |
| 7 | 「範囲を指定してAI分析を依頼 →」（435） | **する** | `/analyze`（SC-08）へのリンク |
| 8 | 注記「LLM不調時は検索結果のみ返す縮退運転」（436） | **する**（挙動として） | 静的な注記ではなく**実際の縮退**として実装する。§エラー・状態 参照 |
| 9 | **対象範囲フィルタ**（タグ／フォルダのチップ・「＋ 絞り込み（権限内のみ）」）（419） | **しない** | **BFF に載せる先が無い**。§実装しない要素の理由 (a) |
| 10 | **「個人資料を含める: ON ⬤」**（419） | **しない** | FR-19 / FR-21。[[IADR-0119]] 決定 1 の着手保留 |
| 11 | **出典行の `👤` ＋「個人資料（自分のみ）」**（431） | **しない** | 同上（FR-19 / FR-21） |
| 12 | 右レール「AIチャットパネル」（438-453） | **しない** | 共通シェルの要素。移行**第 4 段**（[[IADR-0121]] 決定 5） |
| 13 | パンくず「ホーム / 検索・チャット質問」（413） | **しない** | 共通シェルの要素。#452（[[IADR-0124]] 以降の共通シェル作業） |
| 14 | 左ナビ・ブランド・アバター（412・414） | **しない**（既実装） | 共通シェル（`foundation/ui/Layout`）が既に持つ |

### 実装しない要素の理由

**(a) 対象範囲フィルタ（モック #9）— 繰り延べであって放棄ではない。**
計画 §SC-01 は「対象範囲フィルタ（タグ／フォルダ）」を任意入力として挙げ、
「**権限内のタグ／フォルダのみ選択可**」と定めている。しかし現在の BFF 契約では次の 2 点が満たせない。

| 必要なもの | 現在の契約 | 実測 |
| --- | --- | --- |
| 絞り込み条件を AI 回答要求へ載せる | `POST /bff/analysis/ask/stream` の要求は `AnalysisRequest(Question, Scope?)` のみで、属性フィルタを取らない | `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/AnalysisBffEndpoints.cs` |
| **権限内の**タグ／フォルダ候補を得る | 一般利用者が呼べるタグ辞書・フォルダ一覧の BFF エンドポイントが無い（タグ辞書は `/bff/admin/authz`＝管理者限定） | `grep -rn 'MapGroup("/bff' src/{platform,knowledge}/backend` の全 10 グループ |

候補を出せないまま入力欄だけ置くと、**利用者が「権限内のみ提示」という計画の保証を受けられない**。
押しても何も変わらないチップを置くのはさらに悪い。よって本 issue では置かず、
**契約の不足として計画へ環流する**（[feedback/20260804_sc01-03-bff-contract-gaps.md](../../feedback/20260804_sc01-03-bff-contract-gaps.md)）。

**(b) 個人資料まわり（モック #10・#11）— [[IADR-0119]] 決定 1 による着手保留。**
「個人資料を含める」トグルと出典行の `👤`／「個人資料（自分のみ）」は **FR-19（個人資料）・FR-21** に属する。
[[IADR-0119]] 決定 1 は「保留の対象は当該 FR を実現するプロダクトコードと、**その受け入れを担う画面**・API・
データモデルである」と定め、決定 2 は着手条件を前提 ADR の **`Accepted`** 化に置いている。
`ADR-0036` / `ADR-0037` は 2026-08-04 時点で `Proposed` であり、条件は未充足である。
**繰り延べであって放棄ではない**——保留が解けた時点で、SC-19 の実装と同じ段で本画面へ足す。

**組織文書側の `📄` ＋ ラベル「組織文書」は実装する。** 計画 §SC-01「区別の表示方法」が
「同じアイコンとラベルを SC-01（出典表示）・SC-18（グラフのノード）・SC-19（一覧）の 3 か所で用いる」と
定めており、組織文書側だけを先に実装しても表記は変わらない（保留が解けたら `👤` の行が増えるだけである）。
アイコンは**計画が字義どおり glyph を指定している**ため lucide-react ではなく計画の記号を用いる
（§UI 部品 参照）。

## データソース（BFF 境界）

| 用途 | エンドポイント | 呼び出し方 | 認可 | 応答 |
| --- | --- | --- | --- | --- |
| AI 回答（ストリーミング） | `POST /bff/analysis/ask/stream` | `apiStream`（`foundation/api`） | 認証・ABAC（後段） | SSE: `citations` → `token`* → `done`（失敗時 `error`） |
| フィードバック | `POST /bff/feedback` | TanStack Query `useMutation` ＋ `apiFetch` | 認証 | `FeedbackDto` |

- **手書き HTTP クライアントは使わない**（`foundation/api` の `apiFetch` / `apiStream` のみ）。
- **SSE は orval 生成フックに載らない**（生成器は `text/event-stream` を扱わない）。状態の持ち方は [[IADR-0126]] 決定 1・2 を正とする。

## レイアウト / 主要素

```text
┌──────────────────────────────────────────────────────────────┐
│ ナレッジ検索・AI質問                                           │
│ 横断検索と根拠付きAI回答（ストリーミング・出典表示）              │
│ [質問またはキーワードを入力…            ] [送信]                │
│                                    キーワード検索のみ →         │
├──────────────────────────────────────────────────────────────┤
│ ▸ AI回答（ストリーミング）                                      │
│   本文（token を逐次連結）                                     │
│   [👍] [👎]  フィードバック                                     │
├──────────────────────────────────────────────────────────────┤
│ ▸ 出典（クリックで文書詳細／Wikiへ）                             │
│   📄 経費精算規程 v3.2 …  ［組織文書］                          │
│   📖 Wiki: 経費精算FAQ    ［組織文書］                          │
├──────────────────────────────────────────────────────────────┤
│ 範囲を指定してAI分析を依頼 →                                    │
└──────────────────────────────────────────────────────────────┘
```

## 表示・入力項目

| 項目 | 種別 | 必須 | 形式・制約 | 説明 |
| --- | --- | --- | --- | --- |
| 質問・キーワード | `Input` | 必須 | **1 文字以上**（前後の空白を除く）。最大 **1000 文字** | 空・空白のみでは送信ボタンを無効化する |
| AI 回答 | 表示 | — | ストリーミング | `token` を連結。生成中は `role="status"` で通知 |
| 出典 | 表示／リンク | — | 記号 ＋ タイトル ＋ 種別タグ | 文書は `/docs/$id`（SC-03）へ**内部遷移**。Wiki 由来は SC-04 へ |
| 👍 / 👎 | 操作 | — | `up` / `down` | `done` 後のみ有効。`answerId` に紐付ける（FR-08） |

> **最大長について**: 計画 §SC-01 の入力表は「1文字以上、**最大長制限**」とだけ書き、値を定めていない。
> 実装は `maxLength=1000` を置く。根拠は BFF ではなく画面側の暴発防止であり、
> **計画が値を定めた場合はそれに従う**（§未決事項）。

## アクション・イベント

| 操作 | 挙動 | 遷移先 |
| --- | --- | --- |
| 送信 | 直前のストリームを `AbortController` で中断 → `POST /bff/analysis/ask/stream` を購読し、`citations` → `token`* → `done` を反映 | — |
| キーワード検索のみ → | 入力中の語を `?q=` に載せて SC-02 へ（UC-01 代替フロー） | `/search?q=…` |
| 出典クリック | 文書出典は SC-03 へ内部遷移。Wiki 出典は SC-04 へ | `/docs/$id` ／ `/wiki` |
| 👍 / 👎 | `POST /bff/feedback`（`answerId` ＋ `rating` ＋ 質問文） | — |
| 範囲を指定してAI分析を依頼 → | SC-08 へ | `/analyze` |

## 出典の種別判定（📄 と 📖）

`CitationDto` は種別を持たない。判定は `sourceUri` の形で行う。

| 条件 | 記号 | 遷移先 | ラベル |
| --- | --- | --- | --- |
| `sourceUri` が実行時 config の `wikiBaseUrl` で始まる | `📖` | SC-04（`/wiki`） | 組織文書 |
| 上記以外（`sourceUri` の有無を問わない） | `📄` | SC-03（`/docs/$id`） | 組織文書 |

- 判定に用いる `wikiBaseUrl` は**実行時 config**（`platform/frontend/public/config.js`）であり、ビルドへ焼き込まない。
- `wikiBaseUrl` が未設定の環境では全件が `📄` になる（Wiki 由来かどうかを推測しない）。
- **色では区別しない**（[INDEX 決定 21](../../planning/projects/microservices-platform/INDEX.md)）。記号は装飾（`aria-hidden`）とし、意味はタグの文字が担う。

## 権限・表示条件・存在秘匿

- 左ナビ「利用者」グループに常時表示（ロール限定なし。05_screens §共通シェル §アクセス制御の割当）。
- ABAC は後段が適用する。**クライアントはスコープを送らない**（送っても BFF は信用しない）。
- 権限外の文書は出典に現れない。UI は「権限が無い」と「該当が無い」を区別しない（[[IADR-0009]]）。

## エラー・状態

| 状態 | 表示 | 起点 |
| --- | --- | --- |
| `idle` | 回答・出典パネルを描画しない | — |
| `streaming` | 「回答を生成中…」（`role="status"`）＋ 逐次本文 | FR-04 |
| `done` | 本文確定・👍/👎 有効 | FR-04 / FR-08 |
| `error`（SSE の `error` イベント／通信失敗） | `Alert tone="warning"` `role="alert"`：AI 回答を生成できない旨と、**キーワード検索へ切り替える導線**（`/search?q=…`） | **UC-01 例外フロー「LLMが不調な場合は検索結果のみを返す（縮退運転）」** |
| 中断（連投・離脱） | 何も表示しない（`AbortError` は失敗ではない） | — |
| フィードバック送信失敗 | 押下状態を戻す（楽観的更新の取り消し）＋ `Alert tone="danger"` | FR-08 |

**縮退運転の実装形**: 本画面は AI 回答だけを担い、検索結果一覧は SC-02 が担う（モックの導線と同じ）。
したがって「検索結果のみを返す」は、**AI が使えないときに検索結果一覧へ 1 クリックで到達させる**ことで満たす。
一覧をこの画面に埋め込む形は採らない——モックが SC-01 に一覧を描いていないためである。

## i18n

- 文言はすべて Lingui のカタログ（ja / en）へ載せる。`<Trans>`（JSX テキスト）と `useLingui().t`（属性値）を使う。
- `eslint-plugin-lingui` の適用範囲を本 feature へ広げる（#496 の申し送り「適用範囲の拡大は #452」）。
- **記号（`📄` / `📖`）は翻訳しない**（`aria-hidden` の装飾であり、意味はタグの文字が担う）。

## UI 部品（`@platform/ui`）

| 用途 | 部品 | 出所 |
| --- | --- | --- |
| 入力欄 | `Input` ＋ `Label` | #496 で移植済み |
| 送信・👍・👎 | `Button` | 同上 |
| 回答／出典の区画 | `Card` / `CardHeader` / `CardTitle` / `CardContent` | 同上 |
| 縮退・失敗の通知 | `Alert` | 同上 |
| 出典の種別ラベル | **`Tag`（新規）** | 下記 |

**`Tag` を新設した判定**（計画 [13_frontend-stack §shadcn/ui 派生の範囲](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md) の 4 基準）:

| 基準 | 該当 | 理由 |
| --- | --- | --- |
| 1. フォーカストラップ | 非該当 | 非対話要素である |
| 2. 複合キーボード操作 | 非該当 | 同上（フォーカスを受けない） |
| 3. ポータル／ポップアップの配置計算 | 非該当 | 通常フローの `<span>` である |
| 4. `aria-*` の動的な同期を要する開閉状態 | 非該当 | 状態を持たない |

→ **4 基準のいずれにも該当しないため Radix を使わず、ネイティブ HTML（`<span>`）＋ `cva` ＋ `cn()` で実装する。**
既存の `StatusBadge` を流用しない理由は、`StatusBadge` が **`tone` ごとに固定アイコンを描く「状態」の部品**
だからである（INDEX 決定 21 を型で強制する設計）。タグ（「組織文書」「経理」）は状態ではなく**分類の名前**であり、
`Info` アイコンが付くと意味が変わる。hi-fi モックも `tag`（分類。全画面で 120 箇所）と
状態表示（`ok` / `warn` / `err`）を別の語彙として描き分けている。

## 関連仕様

- 作業仕様書: [20260804_issue-502_sc01-03-search-flow.md](../specs/20260804_issue-502_sc01-03-search-flow.md)
- テスト仕様書: [SC-01_search-chat.md](../tests/SC-01_search-chat.md)
- 実装 ADR: [[IADR-0037]]（SSE）／[[IADR-0126]]（SSE の状態管理・URL 状態）
- 計画への環流: [feedback/20260804_sc01-03-bff-contract-gaps.md](../../feedback/20260804_sc01-03-bff-contract-gaps.md)

## 未決事項

1. **対象範囲フィルタ**（§実装しない要素 (a)）。BFF 契約の拡張が要る。環流済み。
2. **個人資料の出典表示・フィルタ**（FR-19 / FR-21）。[[IADR-0119]] の保留解除後に着手する。
3. **質問の最大長**。計画は「最大長制限」とだけ書き、値を定めていない（暫定 1000 文字）。
4. **右レール AI チャットパネル**（移行第 4 段。[[IADR-0121]] 決定 5）と**パンくず**（共通シェル）。
