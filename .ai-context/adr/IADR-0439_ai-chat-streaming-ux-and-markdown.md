---
title: IADR-0439 AI 対話の追従スクロール・停止・再生成・出典・Markdown 描画と脚注方式の対応印
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-08, SC-01, SC-04, SC-19, UC-01, NFR, ADR-0031, IADR-0009, IADR-0037, IADR-0121, IADR-0125, IADR-0126, IADR-0134, IADR-0365, IADR-0435, IADR-0436, IADR-0437]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/05_screens/mockups/hi-fi/sc-01.html
related_specs:
  - ../specs/20260912_frontend-nocturne-and-experience-gaps.md
---

# IADR-0439: AI 対話の追従スクロール・停止・再生成・出典・Markdown 描画と脚注方式の対応印

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: Claude（実装）／次段 4 項目の採否は利用者裁定（2026-09-12 裁定 6）

## 起点・関連

- 関連する計画書 ID:
  SC-01（検索／チャット質問画面）／05_screens（計画リポ）§共通シェル「**AI チャットパネル（右レール）**」・
  §横断の表示規約／FR-04（根拠付き AI 回答）・FR-08（回答フィードバック）／UC-01（検索・質問する）／
  08_data-egress-policy（計画リポ）（外部資産を取りに行かせない）／
  ADR-0031（計画リポ）（§採用技術一覧）
- 関連する実装 ADR:
  [IADR-0037](IADR-0037_llm-sse-streaming.md)（SSE の購読は `foundation/api` の `apiStream`）／
  [IADR-0126](IADR-0126_sse-answer-state-and-search-url-state.md)（**決定 1 = 回答を TanStack Query の
  キャッシュに載せない**。本決定の履歴の置き場所はここから導かれる）／
  [IADR-0365](IADR-0365_sc04-wiki-screen-ledger-and-sanitize.md)（出典の種別判定は**権限内の Wiki 台帳**で行う）／
  [IADR-0121](IADR-0121_spa-stack-migration-staging.md)（決定 5 = 右レールは自前フック）／
  [IADR-0134](IADR-0134_spa-route-code-splitting-boundaries.md)（初期チャンクの ratchet。Markdown 処理系の遅延の根拠）／
  [IADR-0436](IADR-0436_shared-ui-states-panels-and-base-ui-adoption.md)（`Tooltip` の**最初の利用者が本決定である**）／
  [IADR-0009](IADR-0009_wiki-browsing-404-hides-existence.md)（存在秘匿）
- 関連する実装仕様書:
  [20260912_frontend-nocturne-and-experience-gaps](../specs/20260912_frontend-nocturne-and-experience-gaps.md)
- 関連 issue: 本 PR（UI/UX 改善）。検討メモ 第 4 弾「体験の穴」(3) と次段（別途送付）

## コンテキストと課題

右レール AI チャットと SC-01 の回答表示は、SSE のトークンを**素のテキストとして連結して流す**だけだった。
実際に使うと 5 つの穴が出る。

1. **追従スクロールが無い。** 回答が伸びても容器は動かず、利用者が手でスクロールし続ける。
2. **停止できない。** 長い回答を途中で止める手段が無く、離脱するしかない。
3. **やり直せない。** 同じ質問を再入力する必要がある。
4. **Markdown が生のまま出る。** LLM は箇条書き・見出し・コードを返すが、`#` や `-` がそのまま見える。
5. **出典が本文のどこに対応するのか分からない。** 出典は一覧として下に並ぶだけである。

加えて、右レールは**出典を購読していなかった**（SC-01 だけが出典を描いていた）。
出典の型と種別判定は `knowledge` の SC-01 feature の中にあり、foundation から引けなかった。

## 検討した選択肢

| 論点 | 案 | 却下理由 |
| --- | --- | --- |
| 追従 | **自前フック ＋ `MutationObserver`（採用）** | — |
| | shadcn の `MessageScroller` をコピー | **依存配列と `ResizeObserver` を前提**にする。jsdom に `ResizeObserver` が無く単体テストが書けない。依存配列は**申告漏れ**（出典の追加・Markdown の描き直し）で追従が抜ける |
| | `scrollIntoView` を描画のたびに呼ぶ | 利用者が上へ遡っていても**読んでいる箇所を奪う** |
| 対応印 | **脚注方式（採用）** | — |
| | 本文中のハイライト（文字位置に印を打つ） | **サーバは位置を返さない**（`CitationDto` に span が無い）。推測で打つと誤った根拠の提示になる |
| Markdown | **`marked`（動的 import）＋ DOMPurify（採用）** | — |
| | `react-markdown` | 依存が大きく、初期ロードへの影響が読みにくい |
| | 自前パーサ | 未閉じの記法（ストリーミング中の部分文字列）の扱いを自分で持つことになる |
| SC-01 の履歴 | **画面のローカル state（採用）** | — |
| | 右レールの `aiChatStore` を流用 | 画面キー（pathname）ごとの会話をシェルが持つ器である。**古い出典つきの回答を別の場所へ持ち出す**ことになり IADR-0126 決定 1 の趣旨に反する |

## 決定

### 決定 1: 追従スクロールは自前フックで持ち、下端判定は 32px、検出は `MutationObserver`

- `scrollHeight - scrollTop - clientHeight < 32` を「下端に居る」とする。**0 にしない** ——
  小数ピクセルの丸めで「下端に居るのに居ない」と判定され、追従が勝手に止まる（ズーム時に顕著）。
  32px は 1 行ぶん強で、「ほぼ最後まで読んでいる」と「遡っている」を分けるのに十分である。
- 内容の増加は DOM の変化そのものなので、容器を `MutationObserver` で見る。
  **呼び出し側に「何が変わったら追従するか」を申告させない。**
- 利用者が上へ遡ったら追従を止め、下端へ戻ったら自動で再開する。遡り中は「最新へ」ボタンで復帰できる。
- **容器自身の大きさの変化（レール幅の変更）では追従しない。** 内容の変化ではないためで、
  次の内容変化で再び下端へ寄る。

### 決定 2: 停止（`cancel()`）と再生成（`regenerate()`）をフックが返す

- **停止は失敗ではない。** `AbortController.abort()` で中断し、その時点の部分回答を
  `stopped: true` で残す（画面が「（停止）」の印を付ける）。`AbortError` はエラー表示へ落とさない。
- **再生成は直前の入力（質問 ＋ 対象範囲）を ref に保持して再送する。** 入力欄の現在値ではない ——
  利用者が次の質問を打ちかけているときに、それを送ってしまわないためである。

### 決定 3: 出典の型と種別判定を foundation へ移し、右レールも出典を購読する

`sc01-search/types/citations.ts` → `platform/frontend/src/components/ai-chat/citations.ts`。
SC-01 は `@foundation/ai-chat/citations` を import する（**knowledge → foundation は許可方向**）。

- **判定に使う集合（権限内の Wiki 台帳）は呼び出し側が渡す。** 台帳は knowledge ユニットの口であり、
  **foundation は台帳を引かない**（IADR-0365 の判定規則そのものは変えていない）。
- 種別は**アイコン ＋ ラベルの併用**で示す（色だけで意味を持たせない）。

### 決定 4: 対応印は**脚注方式**とする

本文末尾に出典の一覧を `[1]…[n]` で出し、回答本文中の `[n]` 表記を
`<sup><a href="#<prefix>-cite-n">` として脚注へ結ぶ。

- **文字列置換ではなく `marked` の inline 拡張で行う。** 置換にすると、コードスパンの `` `[1]` `` や
  リンク `[1](url)`・参照リンク定義 `[1]: url` を誤って結ぶ。
- **出典の件数を超える番号は結ばない**（行き先の無いアンカーを作らない）。
- アンカー接頭辞は**回答ごとに一意**にする（履歴に複数の回答が並ぶため）。

### 決定 5: Markdown は `marked` を**動的 import** し、`vendor-markdown` チャンクへ束ねる

🔴 `AiChatPanel` は共通シェルが**静的に import する**。ここが `marked` を静的に import すると
**全利用者の初期ロードに Markdown 処理系が乗る**。最初の描画時に動的 import する。
実測: `vendor-markdown` 44,545 B は遅延側に残り、初期ロードへは届かない。

**`dompurify` は `vendor-markdown` へ入れない。** SC-04 の Wiki 本文 sanitize と共有される
遅延チャンクのままにする —— 入れると**Wiki 閲覧だけの利用者にも `marked` が届く**。

### 決定 6: 描画前に必ず sanitize する（多層防御）

回答は LLM の生成物であり、RAG の文脈には取り込み文書（外部データソース由来）が含まれる。
`marked` は HTML を素通しするので**そのまま `innerHTML` へ入れない**。

- DOMPurify の既定（script / style / iframe / イベント属性 / `javascript:`）に加え、
  **メディア（img / picture / source / video / audio）を落とす** ——
  **外部 URL の資産をブラウザに取りに行かせない**（08_data-egress-policy）。
  SC-04 の `sanitizeWikiHtml` と同じ集合である。
- 外部リンク（`http(s)` の絶対 URL）は `target="_blank" rel="noopener noreferrer"` で開く。
  DOMPurify は `target` を既定で落とすので、**sanitize の後**のフックで付ける
  （前に付けても剥がれる）。断片リンク（脚注）と相対パスは触らない。
- **ストリーミング中の部分文字列を描いてよい。** `marked` は閉じていない強調・フェンス・表でも
  例外を投げず読めるところまで描く（テストで固定）。

### 決定 7: SC-01 の履歴は画面のローカル state に閉じる（直近 10 件）

IADR-0126 決定 1（回答をキャッシュに載せない）と同じ理由による ——
**古い回答が古い出典つきで復活すると、単なる古さではなく誤った根拠の提示になる。**
離脱で消えるのが正しい。件数 10 は計画が定めておらず、**画面 1 枚に収まる目安**である。

### 決定 8: コピーは `navigator.clipboard` ＋ 通知。**設定 UI は置かない**

- コピーの成否は `notify`（sonner）で伝える。成功が見えないコピーは押したか分からない。
- 🔴 **モックの `.rr-set`（モデル・越境のチップ）・`.rr-cfg`（AI 設定）・`.rr-note`
  （画面コンテキスト自動添付・履歴の保存／復元）は描かない。**
  `/bff/analysis/ask/stream` の要求本文は `question` と `attributeFilters` だけであり、
  **これらを送る先が契約に無い**。置くと「操作できるのに何も変わらない」設定になる。
  履歴もメモリ上にしか無い（リロードで消える）——**そのとおりに書く**。
  契約が追いついた時点で足す。**繰り延べであって放棄ではない。**

## 理由

- 決定 1・2 は「**利用者から制御を奪わない**」という 1 つの基準から出ている（読んでいる箇所を奪わない／
  止められる／やり直せる）。
- 決定 4 は**サーバが位置を返さない**という契約上の事実からの帰結であり、設計の好みではない。
  位置が返るようになれば本文中の印へ移せる。
- 決定 5・6 は既存の制約（初期ロードの ratchet・egress ポリシー）へ素直に従っただけである。
- 決定 8 は**動かない UI を置かない**という本リポジトリの既存の作法（同じ判断が右レールの初回実装でも
  行われている）の踏襲である。

## 結果

- 良い影響:
  - 右レールと SC-01 が**同じ部品と同じ型**で出典・Markdown・コピーを扱うようになった。
  - 長い回答が読めるようになった（追従・停止・再生成）。
- 悪い影響・トレードオフ:
  - 🔴 **`Tooltip` の最初の利用者になったため、`vendor-baseui` 85,457 B が初期ロードへ入った。**
    右レールのアイコンボタン（履歴の消去・全消去・閉じる・コピー）に説明が要るためである。
    `React.lazy` 越しでも消えない辺である（IADR-0436 決定 3）。床を 617,157 → 656,554 B へ更新した
    （A/B の切り分けは `scripts/chunk-budget-baseline.json` の該当項）。
  - `MutationObserver` は**内容の変化を細かく拾う**。長い回答では発火回数が多い
    （実測上の問題は出ていないが、計測はしていない）。
  - 対応印は**モデルが `[n]` を書いたときだけ**効く。書かなければ脚注一覧が並ぶだけである。
- フォローアップ:
  - 契約が位置情報を持ったら、脚注方式から本文中の印へ移せるか判断する。
  - 契約がモデル選択・越境設定を持ったら、右レールの設定 UI を足す。

## 関連

- Supersedes: なし。
- Superseded by: なし。
