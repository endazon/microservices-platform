---
title: IADR-0126 画面のサーバー状態の持ち方 — SSE 回答はミューテーション＋ローカル蓄積、検索条件は URL 単一情報源
type: impl-adr
status: Accepted
related_ids: [SC-01, SC-02, SC-03, UC-01, FR-03, FR-04, FR-08, ADR-0031, IADR-0037, IADR-0121, IADR-0124, IADR-0125]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - ../specs/20260804_issue-502_sc01-03-search-flow.md
  - ../screens/SC-01_search-chat.md
  - ../screens/SC-02_search-results.md
  - ../screens/SC-03_document-detail.md
  - ../adr/IADR-0037_llm-sse-streaming.md
  - ../adr/IADR-0121_spa-stack-migration-staging.md
---

# IADR-0126: 画面のサーバー状態の持ち方 — SSE 回答はミューテーション＋ローカル蓄積、検索条件は URL 単一情報源

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: **SC-01 / SC-02 / SC-03**・**UC-01**・FR-03 / FR-04 / FR-08 ／
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted。
  **サーバー状態 = TanStack Query**）
- 関連する技術検討（計画）:
  [13_frontend-stack](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)
  §リスク・未決事項 —— 「**右レール AI チャットパネル（SSE ストリーミング）の状態管理パターン
  （TanStack Query の streamedQuery か自前フックか）は実装時に検証する**」。
  **計画が明示的に実装へ委ねた論点であり、本 IADR がその答えである。**
- 関連する実装 ADR: [[IADR-0037]]（SSE を `EventSource` ではなく `fetch` ＋ `ReadableStream` で購読する）／
  [[IADR-0121]] 決定 3・8（BFF 呼び出しの出口は `foundation/api` の 1 箇所・Redux 不採用）／
  [[IADR-0124]]（型付きルート）
- 関連する実装仕様書: [20260804_issue-502](../specs/20260804_issue-502_sc01-03-search-flow.md)
- 関連 issue: #502（SC-01〜03 の再実装）／ 親 #452 / #454

## コンテキストと課題

`CLAUDE.md`（技術スタック別ルール）と [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md) は
「**サーバー状態は TanStack Query に一元化する**」と定めている。SC-02（検索結果）・SC-03（文書詳細）は
素直に `useQuery` へ載る。しかし **SC-01 の AI 回答は載らない**——応答が 1 個の値ではなく、
`citations` → `token`* → `done` と**時間をかけて増えていく系列**だからである。

さらに SC-02 には旧実装が抱えていた具体的な不具合の芽がある。検索語が
**入力欄のローカル state と URL の `?q=`** の 2 か所にあり、送信時の直接実行と `?q=` 変化の `useEffect` が
二重に発火する。旧実装はこれを `lastSearched` という 3 つ目の state（直近に実行した検索語）で抑えていた。
再実装で同じ構造を持ち込むかを決める必要がある。

決めるべきは次の 3 点である。

1. SSE で届く AI 回答をどの器に入れるか。
2. SC-02 / SC-03 の取得を `useQuery` にする場合、キャッシュキーと有効化条件をどう置くか。
3. 検索語の単一情報源をどこにするか。

## 検討した選択肢

### 論点 1: SSE 回答の器

| | A. `useMutation` ＋ ローカル蓄積（採用） | B. `useQuery`（`queryFn` の中で購読） | C. TanStack Query の `streamedQuery` | D. 素の `useState` ＋ `useRef`（旧実装） |
| --- | --- | --- | --- | --- |
| 送信＝副作用として表現できるか | できる（`mutate` は明示的な発火） | **できない**（`useQuery` は宣言的な取得であり、キーが同じなら再取得を勝手にまとめる） | できる | できる |
| キャッシュに載せてよいか | 載せない（回答は毎回作られる。同じ質問でも別の回答） | **載る**（不適切。戻る操作で古い回答が復活する） | 載る | 載らない |
| 中断（連投・離脱） | `AbortController` を明示的に扱える | `signal` は渡るが、途中経過の破棄が書きにくい | 扱える | 扱える |
| 途中経過（token 連結） | ローカル state に蓄積 | キャッシュ更新を毎 token 行うことになる | ライブラリが蓄積する | ローカル state |
| 依存 | 既存（`@tanstack/react-query`） | 同左 | **`@tanstack/react-query` の experimental API** | 無し |
| 「サーバー状態は TanStack Query」への適合 | 適合（要求の発火・進行・失敗を Query が持つ） | 形は適合するが意味が合わない | 適合 | **不適合** |

### 論点 3: 検索語の単一情報源

| | A. URL（`?q=`）のみ（採用） | B. ローカル state のみ | C. 両方＋同期ガード（旧実装） |
| --- | --- | --- | --- |
| 共有・ブックマーク | できる | **できない** | できる |
| 戻る／進む | 効く | **効かない** | 効く |
| 二重発火 | **構造的に起きない**（発火点が 1 つ） | 起きない | 起きる。ガード（`lastSearched`）で抑える |
| 実装量 | 少ない | 少ない | 多い（3 つ目の state と、その正しさを見るテスト） |

## 決定

1. **SSE の AI 回答は `useMutation` で発火し、途中経過はコンポーネントのローカル state に蓄積する**
   （論点 1・選択肢 A）。購読は `foundation/api` の `apiStream`（[[IADR-0037]]）を使い、
   `AbortController` で前のストリームを中断する。**回答は TanStack Query のキャッシュに載せない。**
2. **フィードバック送信（`POST /bff/feedback`）も `useMutation`** とする。楽観的に押下状態を反映し、
   失敗したら戻す。
3. **SC-02 の検索語は URL（`?q=`）を単一情報源とする**（論点 3・選択肢 A）。入力欄は未確定の編集値であり、
   取得の引き金にはならない。送信は `navigate({ to: '/search', search: { q } })` だけを行う。
4. **SC-02 / SC-03 の取得は `useQuery`** とし、キーは `['bff', <資源>, …]` の形で BFF のパスに対応させる。
   **SC-03 の版履歴は詳細の成功後にだけ有効化する**（`enabled`）——詳細が 404（存在秘匿）のときに
   版履歴だけを叩くのは、確実に 404 になる往復である。
5. **`QueryClient` を feature で作らない。** `foundation/api/queryClient.ts` が唯一の生成点である
   （[[IADR-0121]]）。テストのハーネス（`foundation/testing/renderUnitRoute`）は
   **描画のたびに新しい `QueryClient`** を作り、テスト間でキャッシュを共有しない。
6. **`streamedQuery` は使わない**（論点 1・選択肢 C）。experimental API であり、
   本 issue が必要とする「中断つき単発ストリーム」に対して得るものが無い。
   計画の未決事項（13_frontend-stack §リスク）に対する答えは **「自前フック側」＝ 決定 1** である。

## 理由

- **回答はキャッシュしてよい種類の状態ではない。** 同じ質問でも回答は毎回生成され、`answerId` が変わる。
  `useQuery` に載せると、戻る操作や再マウントで**古い回答が出典つきで復活する**。
  出典は「いま表示している回答の根拠」であるため、これは単なる古さではなく**誤った根拠の提示**になる。
- **`useMutation` を選ぶのは形式合わせではない。** 送信は利用者の明示的な操作（副作用）であり、
  進行中・失敗・完了という状態を画面が必要とする。TanStack Query の mutation はまさにその器で、
  「サーバー状態は TanStack Query に一元化」という規約の意味（**状態を自前で組み立て直さない**）にも合う。
  蓄積される token 列だけがローカルにある——これは**まだサーバー状態ではない途中経過**である。
- **URL を単一情報源にすると、二重発火の問題が構造的に消える。** 旧実装のガード（`lastSearched`）は
  「2 つの発火点があること」を前提に、その帰結を打ち消す仕掛けだった。発火点を 1 つにすれば
  ガードもガードのテストも要らない。加えて計画（05_screens §ルートパス）が `/search?q=` を
  **URL に条件を載せる形**で定めており、URL を正にすることは計画の形をそのまま実装することでもある。
- **版履歴の `enabled` は存在秘匿と整合する。** 詳細が 404 の文書に対して版履歴を叩いても、
  BFF は同じ 404 を返す（`FetchAuthorizedAsync` が先に走る）。要求を出さないことで、
  「404 が 2 回出る」というログ上の無駄と、画面側での二重のエラー処理を避けられる。

## 結果

- 良い影響:
  - SC-01 の連投（前の回答を捨てて新しい質問を投げる）が `AbortController` の 1 箇所で表現される。
  - SC-02 の状態が「URL ＋ 入力欄」の 2 つになり、旧実装の 4 つ（`input` / `status` / `response` / `lastSearched`）から減る。
  - 検索結果は `staleTime`（30 秒。`DEFAULT_QUERY_OPTIONS`）の範囲で再利用され、
    SC-02 → SC-03 → 戻る の往復で再検索しない。
- 悪い影響・トレードオフ:
  - SC-01 の回答は再マウントで消える（キャッシュしないため）。**これは意図した挙動**であり、
    履歴が要るなら右レールの画面別履歴（移行第 4 段。[[IADR-0121]] 決定 5）が担う。
  - `useMutation` と `apiStream` の組み合わせは、TanStack Query の再試行（`retry`）に載らない。
    SSE の途中で切れた場合の再試行は行わない——途中まで表示した回答を捨てて最初からやり直すか、
    継ぎ足すかは利用者に見える挙動であり、計画の裁定が要る（§フォローアップ）。
- フォローアップ:
  1. 右レール AI チャットパネル（第 4 段）を作るときは、本 IADR の決定 1 をそのまま流用できるかを確認する。
     複数の会話を保持する要求（画面別履歴）が入るため、ローカル state では足りなくなる可能性がある。
  2. SSE 中断時の再試行方針を計画へ問う（環流の候補）。
  3. `streamedQuery` が stable になった時点で、決定 6 を再評価してよい。

## 関連

- Supersedes: なし
- Superseded by: なし
