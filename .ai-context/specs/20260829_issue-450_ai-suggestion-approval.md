---
title: 作業仕様書 — SC-03 の AI 提案承認欄と BFF の承認・却下口（#450）
type: spec
status: done
related_ids:
  - FR-05
  - FR-17
  - FR-18
  - UC-10
  - SC-03
  - SC-21
  - ADR-0033
  - ADR-0034
  - ADR-0050
author: claude
created: 2026-08-29
updated: 2026-08-29
plan_refs:
  - "planning:projects/microservices-platform/05_screens/01_screens.md §SC-03「AI 提案の承認欄」／§AI 提案の承認 UI"
  - "planning:projects/microservices-platform/07_adr/ADR-0033_knowledge-graph-data-model-and-store.md 決定 7・9・10"
  - "planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md 決定 2・8"
related_adrs:
  - IADR-0300
  - IADR-0276
  - IADR-0272
  - IADR-0135
  - IADR-0124
  - IADR-0009
  - IADR-0044
---

# 仕様書: SC-03 の AI 提案承認欄と BFF の承認・却下口

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-18（AI によるリンク・タグ提案と人手の承認）／FR-17（文書間リンク）／FR-05（ABAC）
- ユースケース（UC）: UC-10 代替フロー（**承認が確定するのは SC-03 経由のみ**）
- 画面（SC）: SC-03（**承認の主導線**）／SC-21（棚卸しの従。本作業では変更しない）
- 関連 ADR: ADR-0033 決定 7（3 状態）・決定 9（型名は辞書で解決）・決定 10（却下と解除）／
  ADR-0034 決定 2（権限外も不存在も 404）・決定 8（終点に課すのは閲覧権限）／ADR-0050（本文指紋）
- 計画書リンク: 隣接クローン `../project-planning` の `projects/microservices-platform/05_screens/01_screens.md`
  （`:242` の「AI 提案の承認欄（確定・2026-08-02）」と `:808` の「AI 提案の承認 UI」節）

計画の確定事項（`:242`〜`:247` の逐語）:

- 当該文書を**両端のいずれかとする提案**（リンク候補・**タグ候補**）を本文の下部に表示し、**その場で承認／却下できる**
- **提案が 0 件のときは欄自体を表示しない**
- 各提案には**種類（リンク／タグ）・相手の文書またはタグ・辺の型・提案の根拠**を示す
- 本欄に既定で表示するのは `pending` の提案である
- 本欄から SC-21 への導線を置く

## 目的・背景

GraphService には承認（`POST /graph/suggestions/{id}/approve`）・却下（同 `/reject`）が既にあるが、
BFF に出ているのは一覧 1 本だけである（IADR-0276 決定 1 が「承認欄と同じ変更単位で開ける」として
意図的に閉じた）。その承認欄が本作業の射程であり、**BFF の口・契約・生成物・画面・テストを同じ変更単位に置く。**

## 対象範囲

- 対象: BFF の承認・却下口 2 本／`docs/api/openapi.yaml` の 2 パス追記と orval 生成物／
  SC-03 の承認欄（`DocumentDetailPage`）と i18n／BFF テストの組み替え／画面・テスト仕様書の追随／IADR-0300
- 対象外（理由つき）:
  - **GraphService の後段実装**（`src/knowledge/backend/Services/GraphService/**`）。並行トラックが
    保持しており、本作業では 1 行も触らない。後段の承認・却下・生成は既に在るため、触る必要も無い
  - **生成口（`POST /graph/suggestions/generate/{documentId}`）の BFF 公開**。計画に「利用者が生成を
    起動する」導線が無く（SC-03 も SC-21 も生成ボタンを持たない）、消費者の無い書き込み口を開けない
    という IADR-0276 決定 1 の理由がそのまま残る。**後段の注記どおり `analyze` アクションの裁定待ちでもある**
  - **一覧への `documentId` 絞り込みの追加**。後段（GraphService）の変更を伴い、上記のとおり対象外である
  - **SC-21 の画面**。書き込みを一切しない画面という位置づけは変わらない
  - `docs/api/BFF_bff-surface.md` のエンドポイント一覧（後述 §母集合 軸 4 の除外理由）

## 母集合の引き方・結果・除外理由

`.claude/rules/traceability.repo.md` §是正・追随の母集合の取り方 に従い、**記憶で挙げず、誤りの側の
文字列で全文書を走査してから挙げた**（規則 9）。走査は `src/ai-stock-trading`（別プロジェクトの
submodule）・`node_modules`・`obj` / `bin`・生成カタログ（`locales/`）を除外して行った。

### 軸 1 — 「承認・却下の口は BFF に無い／読み取りだけである」と書いた箇所

走査 1: `grep -rn "承認・却下" .`（ファイル数 17）／
走査 2: `grep -rn "読み取り口だけ\|読み取りだけ\|読み取りのみ" .`（ファイル数 19。うち AI 提案に
関係するのは 5）。突き合わせた結果、**本作業で追随が要るのは次の 5 件**である。

| # | 箇所 | 現状の記述 | 対応 |
| --- | --- | --- | --- |
| 1 | `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/GraphBffEndpoints.cs` | 「開けるのは読み取り口だけである」「承認・却下・生成は BFF へ公開しない」 | 承認・却下を開けた事実へ書き換え、**生成だけが閉じている**理由へ絞る |
| 2 | `src/platform/backend/Bff/Platform.Bff.Tests/BffGraphSuggestionTests.cs` | ルート表の走査で `methods == ["GET"]` を主張 | **主張を組み替える**（§テスト方針） |
| 3 | `docs/api/openapi.yaml` `/bff/graph/suggestions` の `description` | 「読み取りだけである。承認・却下の口は BFF に無い」 | 追記のみ可の制約下で、**当該 2 行を事実へ書き換える**（行の削除ではない） |
| 4 | `docs/screens/SC-21_ai-suggestion-list.md`（4 箇所: `:21` `:110` `:137` `:150-151`） | 「導線の先にある承認欄はまだ無い」「承認・却下の口を公開 API にも置いていない」 | 実態へ是正 |
| 5 | `docs/tests/SC-21_ai-suggestion-list.md`（`:24`） | 「文書詳細画面の承認欄（別 issue）」を対象外に挙げる | 実態へ是正 |
| 6 | `docs/screens/SC-03_document-detail.md` / `docs/tests/SC-03_document-detail.md` | 対応表 #7〜#9 が「しない」・未決事項 1 が「着手は #452」 | #8 を「する」へ。**#7 / #9 は「しない」のまま**（グラフ導線は本作業の射程外） |

**除外した箇所と理由**:

- `.ai-context/specs/*`（`20260823_issue-918_*` ほか 4 件）— **凍結記録**であり、本文プロズを後から
  書き換えない（`.claude/rules/traceability.repo.md` §凍結の射程）。経過追記も本作業では要らない
  （当時の記述は当時の事実として正しい）
- `.ai-context/adr/IADR-0276` — 凍結記録ではなく **live な権威文書**なので、決定 1 へ
  `［2026-08-29 追記 / #450］` の日付つきブロックを足し、後継の IADR-0300 を隣に置く。
  **決定 1 の本文（旧記述）は残す**（ID を後継へ付け替えない。`#580` の書式）
- `.ai-context/adr/README.md` — 本作業では**追記のみ**（親が union で解決する制約）。IADR-0276 行の
  タイトルセルは書き換えない
- `src/knowledge/frontend/src/features/sc21-ai-suggestions/**` — 「本画面は書き込みをしない」は
  **本作業後も真**である（承認は SC-03 に置いた）。追随不要
- `src/knowledge/backend/Services/GraphService/**` — 触ってはならない領域。かつ後段の記述
  （「一括承認の口を置かない」）は本作業後も真である

### 軸 2 — 「一括承認はどの層にも作らない」の固定（**壊してはならない側**）

走査: `grep -rn "一括承認" .` → 18 ファイル。うち**機械で固定しているのは 3 件**である。

| 箇所 | 固定の形 | 本作業での扱い |
| --- | --- | --- |
| `BffGraphSuggestionTests.No_write_route_for_suggestions_is_exposed_by_the_bff` | ルート表の走査（`methods == ["GET"]`） | **主張を「一括承認のパターンに一致するルートが無い」へ組み替える**（固定は残す） |
| `GraphService/Tests/AiSuggestionEndpointsTests`（3 箇所） | 後段のルート表走査 | 触らない（後段は変わらない） |
| `sc21-ai-suggestions/components/AiSuggestionListPage.test.tsx`（2 箇所） | 画面に一括操作が無いこと | 触らない |

**SC-03 側にも同型の固定を新設する**（承認欄が新しい「一括承認を置ける場所」になったため）。

### 軸 3 — 規則 10（是正で**新たに**誤りになる自分の記述）

本作業の是正語（「承認・却下を BFF へ開けた」）で引き直した結果、次が新たに誤りになる。

- `GraphBffEndpoints` 冒頭の「**開けるのは読み取り口だけである**」— 軸 1 #1 に含む
- `openapi.yaml` の `/bff/graph/suggestions` の説明「**読み取りだけである**」— 軸 1 #3 に含む
- `DocumentDetailPage.tsx` 冒頭の「**AI 提案の承認欄（FR-18）と SC-18 への導線（FR-17）は実装しない**」
  — 承認欄だけが実装されるので、**2 つを 1 文で否定している形が誤りになる**。導線の否定は残す
- `DocumentDetailPage.test.tsx` の否定テスト — 5 語を 1 本で否定しており、**承認欄を足すと落ちる**。
  分割する（§テスト方針）
- 上記テスト直前のコメント「**ここに起点 ID を書かないのは意図的である**」— 承認欄は着手したので、
  **起点 ID を書く側へ差し替える**（`check-test-traceability.js` の誤報の前提が消える）

導出値（件数）は**走査ではなく計算し直した** —— 本書の「17 ファイル」「19 ファイル」「18 ファイル」は
2026-08-29 の実測である。

### 軸 4 — 除外した追随先と理由

- `docs/api/BFF_bff-surface.md` §エンドポイント一覧: **`/bff/graph/*` の 4 本も `/bff/private-notes*` の
  11 本も、そもそも 1 行も載っていない**（`grep -n "graph" docs/api/BFF_bff-surface.md` が 0 件。
  openapi の `/bff/` パスは 58 本、本表は約 60 行だが群がずれている）。本作業の 2 本だけを足すと
  「graph 群は承認・却下だけが在る」と読める表になる。**群ごとの欠落は本作業より前からある別件**であり、
  追随 issue に回す（§未決事項 3）
- `perf/` `deploy/` — `/bff/graph` を参照する定義が 0 件（走査で確認）

## 設計

### D-1: BFF に承認・却下を開ける（転送器を足す）

`GraphBffEndpoints.ProxyAsync<T>` は `client.GetAsync` を直に呼ぶ **GET 専用**である。
`PrivateNoteBffEndpoints` の `ForwardAsync(HttpMethod, path, body, …)` ＋ `RelayAsync` と同型の
転送器を `GraphBffEndpoints` へ足す（`/{id:guid}/restore` が本文なし POST の完全な同形）。

- **権限伝播は方式 A（Authorization ヘッダの伝播）を維持する。** GraphService は自分で JWT から
  ABAC を解決する型（`GraphAccessResolver`）であり、`SearchBffEndpoints` の方式 B（解決済み scope を
  本文へ載せる）を持ち込むと**その経路へ到達できる誰もが任意の scope を主張できる**
- **BFF 側に要求するロールは無い**（`x-roles: []`）。後段が 4 段の門
  （Read scope → 存在 → 両端の `AuthorizedNode.Authorize` → Write scope ＋ `IsSourceWritableAsync`）を持つ
- 🔴 **状態コードは作り替えず透過する。** 404 を 403 や 200 へ変換すると存在秘匿が BFF 層で破れる
- **前段の write ゲート（`ForwardIfWritableAsync` 相当）は置かない。** 判断と根拠は IADR-0300 決定 2

### D-2: 却下は**本文を送らない**

後段の `RejectAiSuggestionRequest`（両端の本文指紋）は**任意**である。SPA は指紋を持てない
（`AiSuggestionDto` は指紋を公開面へ出さない。IADR-0276 決定 2）ので、BFF は本文なしで転送する。
帰結は IADR-0300 決定 3 に実測つきで書く（**解除の発火条件は変わらない**）。

### D-3: SC-03 の承認欄

- データ源は `GET /bff/graph/suggestions?state=pending`。**後段に文書での絞り込みが無い**ため、
  当該文書を端点とする提案への絞りは**画面側で行う**（`sourceDocumentId === id || targetDocumentId === id`）。
  後段への絞り込み追加は GraphService の変更であり本作業の対象外（§未決事項 1）
- 辺の型名は**辞書（`/bff/graph/edge-types`）で解決する**（ADR-0033 決定 9）。SC-21 と同じく
  feature 境界を越えないため 5 行の重複を選ぶ（IADR-0262 決定 4）
- **0 件なら欄自体を描かない**（計画の逐語）
- **タグ提案の承認は不可として描く**（`disabled` ＋ 理由。却下は可）。判断と根拠は IADR-0300 決定 4
- 本欄から SC-21 への導線を置く
- 🔴 **一括承認・一括却下の操作を置かない**

## 受け入れ基準

- [x] `POST /bff/graph/suggestions/{id}/approve` と `/reject` が在り、Authorization を後段へ伝播する
- [x] 後段の 404 / 409 / 400 を**作り替えずに**透過する（本文ごと）
- [x] 一括承認のパターンに一致するルートが BFF に 1 本も無い（ルート表の走査で固定）
- [x] `docs/api/openapi.yaml` に 2 パスが `x-roles: []` つきで載り、`check-bff-authz-docs.js` が通る
  （BFF 端点 74 → **76**）
- [x] `pnpm run codegen` の再生成で差分が出ない（生成物の md5 が 2 回の実行で一致）
- [x] SC-03 に承認欄が出る（`pending` かつ当該文書が端点のものだけ／0 件なら欄ごと出さない）
- [x] SC-03 に**知識グラフ導線が無い**ことは引き続き固定されている（否定テストを分割して残した）
- [x] i18n（ja / en）に未翻訳が無い（新規 11 件を en へ訳出）
- [x] 変異試験 **9 件**が全 KILL、無変異のベースラインが緑（§変異試験の結果）

## 変異試験の結果

無変異のベースライン: BFF 21 / 21 緑・SC-03 画面 20 / 20 緑。

| # | 変異 | 期待 | 結果 | 落ちたテスト |
| --- | --- | --- | --- | --- |
| M1 | `ForwardAsync` の Authorization 伝播を落とす | KILL | **KILL**（6 件） | `Approve_forwards_credentials_and_relays_the_body` ほか |
| M2 | `RelayAsync` で 404 を 403 へ変換する | KILL | **KILL**（2 件） | `Backend_404_is_relayed_verbatim`（approve / reject） |
| M3 | 一括承認の口 `/suggestions/approve-all` を開ける | KILL | **KILL**（1 件） | `No_bulk_approval_route_for_suggestions_is_exposed_by_the_bff` |
| M4 | タグ提案でも承認ボタンを押せるようにする | KILL | **KILL**（1 件） | `shows a tag suggestion but disables approval` |
| M5 | `RelayAsync` が応答本文を捨てる | KILL | **KILL**（3 件） | `…relays_the_body` / `Backend_409…` / `Backend_400…` |
| M6 | 当該文書での絞りを外す | KILL | **KILL**（1 件） | `only renders suggestions that touch this document` |
| M7 | 0 件でも欄を描く | KILL | 🔴 **初回 SURVIVE → テストを直して KILL** | `does not render the suggestion panel when there is nothing pending` |
| M8 | 提案の取得失敗を「提案が無い」へ縮退させる | KILL | **KILL**（1 件） | `does not degrade a failed suggestion fetch into an empty panel` |
| M9 | 後段パスに末尾スラッシュを付ける | KILL | **KILL**（6 件） | `It_calls_the_backend_path_without_a_trailing_slash` ほか |

### 🔴 M7 が最初に生き残った理由（装置の欠陥。記録する）

「0 件なら欄を出さない」の否定テストが**提案の取得の完了を待たずに `queryBy*` で否定していた**。
`isPending` の間は欄を出さない実装なので、**0 件でも読み込み中でも同じ「無い」が観測される** ——
すなわち当初のテストは「読み込み中に出さないこと」しか測っていなかった。

**待つ合図が無いのが本質である**（欄が出ないので「現れるのを待つ」ことができない）。
`renderUnitRoute` が返す `queryClient.isFetching()` が 0 になるまで待つ形へ直した。
**「無いこと」を固定するテストは、非同期の取得を挟むと自明に緑になりやすい。**

## テスト方針

### BFF（xUnit / `BffGraphSuggestionTests`）

- 既存の `No_write_route_for_suggestions_is_exposed_by_the_bff` を**主張ごと組み替える**。
  「メソッドが GET だけ」ではなく「**一括承認のパターン（`approve-all` / `bulk` / `{id}` を持たない
  承認パス）に一致するルートが無い**」を主張し、陽性対照として**単票の承認・却下が在ること**を測る
- 新規: 承認・却下の 200 透過／Authorization の伝播（`LastGraphForwardedAuthorization`）／
  後段パスの一致／404・409・400 の透過／未認証 401／後段不達の 502
- スタブ（`GraphStubHandler`）に**メソッドと本文の観測点**と単票応答を足す

### フロント（Vitest / `DocumentDetailPage.test.tsx`）

- 既存の否定テストを**分割する**。「AI 提案パネル」の否定は落とし、**知識グラフ導線の否定は残す**
  （`/知識グラフ/`・`role=link name=/グラフ/`）。前提の `mocks.wikiBaseUrl` を置く作法
  （導線の並びを全部描かせてから否定する）は新テストにも引き継ぐ
- 新規: 承認欄の描画（リンク提案とタグ提案）／0 件で欄ごと出ない／当該文書と無関係の提案を描かない／
  承認・却下の要求が正しいパスへ出る／タグ提案の承認が押せない／一括操作が無い／SC-21 への導線

### 変異試験

検出力を示すため最低 5 変異を入れ、**全 KILL** を実測する（§報告）。

## 計画書との差異

- 差異: **あり**。計画 `:243` は「当該文書を両端のいずれかとする提案（リンク候補・**タグ候補**）を
  …**その場で承認／却下できる**」と定めるが、**タグ提案の承認は後段が実質 no-op** である
  （`AiSuggestionEndpoints` は `Kind == Link` のときだけ辺を作り、タグは「反映の経路は #918 で決める」と
  書いたまま #918 が一覧のみで着地した）。**承認しても文書のタグは 1 つも増えない。**
  - 対応: **表示はする（計画どおり）／承認だけを不可として理由を示す／却下は可**とした。
    根拠は IADR-0300 決定 4。**計画へ環流する**（§未決事項 2 に issue 草案）

## 未決事項

1. **一覧に文書での絞り込みが無い。** SC-03 は権限内の `pending` 全件を取得して画面側で絞る。
   件数が増えると無駄が大きい。後段（GraphService）へ `documentId` の絞りを足す追随 issue が要る
2. **タグ提案の承認に反映先が無い。** 計画は「その場で承認／却下できる」と定めるが、反映経路が
   実装されていない。計画リポジトリへ裁定依頼を起票する（本作業では画面側で承認を塞ぐ）
3. **`docs/api/BFF_bff-surface.md` のエンドポイント一覧が群ごと欠落している**（graph 4 本・
   private-notes 11 本）。本作業より前からの欠落であり、別 issue で一括して埋める
4. **`#452` の指し先が二重になっている。** IADR-0276 と `DocumentDetailPage.tsx` は SC-03 の承認欄を
   `#452` と書くが、`#452` は SC-12（MCP クライアント管理・IADR-0297）として 2026-08-28 に着地した。
   本作業は親 issue `#450` の下で行い、**新しい記述では `#450` を使う**。過去の記録は書き換えない
