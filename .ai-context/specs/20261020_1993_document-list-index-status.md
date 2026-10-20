---
title: 作業仕様書 — 文書管理の一覧に索引の状態の列（索引済み・埋め込み待ち・未索引）を出す（取り込み側の gRPC 面と BFF の結合）
type: spec
status: in-progress
related_ids: [FR-06, FR-02, SC-05, UC-03, NFR-09, NFR-16, ADR-0029, ADR-0075, ADR-0127, IADR-0009, IADR-0041, IADR-0045, IADR-0379, IADR-0401, IADR-0402, IADR-0533, IADR-0536, IADR-0537, IADR-0545, IADR-0561, IADR-0565]
author: claude
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（文書管理画面「一覧の送り方と件数」）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-06）
issue: "#1993"
---

# 作業仕様書 — 文書管理の一覧の索引の状態の列（#1993）

> 本仕様書は実装着手前に作成した（着手 2026-10-20）。判断の記録は **IADR-0565** に置く。
> 基点は MSP `origin/develop` `fe06bdee`（#1923 の残りの #1990・#1992 のマージ後）。

## 起点

- 起点 issue: **#1993**。#1923 の依頼 5「列の見直し」のうち、#1990・#1992 で残った「索引の状態」の列。
- 計画: §文書管理画面「一覧の送り方と件数」（2026-10-16 補完）は一覧の要素（ページ・並べ替え・表題の部分一致・状態の絞り込み）と
  「件数はすべて ABAC で絞った後の数」を定めるが、**列の一覧と索引の状態の分類は定めていない**（実測 2026-10-20・planning `main`）。
  → **状態の分類（3 値と、引けなかったときの「不明」）は実装の判断であり、IADR-0565 に根拠を残す。** 計画へ写さない（IADR-0561 と同じ扱い）。
- 計画の受け入れ基準（FR-06）は文書の CRUD・版・属性であり、本件は一覧の表示の追加なので受け入れ基準を変えない。

## 受け入れ基準（issue の案を写す）

| # | 基準 | 試験 |
| --- | --- | --- |
| A1 | ページ分の文書について、状態が 3 値（索引済み・埋め込み待ち・未索引）のどれかで出る | 取り込み: 分類の純関数・gRPC 面。BFF: 結合。画面: 列 |
| A2 | 取り込み側が応答しないときは「不明」を出し、一覧そのものは失敗させない | BFF: UNAVAILABLE・INTERNAL・期限切れ・トークン取得失敗で 200 ＋ 全件 `unknown`。画面: 「不明」 |
| A3 | 権限外の文書の状態は問い合わせない（存在秘匿 IADR-0009） | BFF: 取り込みへ渡した ID の集合 ＝ 窓の中の（ABAC を通った）ID。権限外を混ぜた台帳と混ぜない台帳で応答と問い合わせが一致 |
| A4 | 照会は 1 ページ分の ID を 1 回でまとめて行う（N+1 にしない）。上限を設ける | BFF: 1 要求につき rpc 1 回。取り込み: 上限（100）超は `INVALID_ARGUMENT`。Qdrant への問い合わせ数は ID の数に依らない |
| A5 | 参照実装の 4 決定（deny-by-default・grpcPort・realm/audience・proto 検査）を写す | 下の「参照実装の 4 決定」 |
| A6 | 色だけで意味を持たせない | 画面: `StatusBadge`（tone ごとの固定アイコン＋文言） |

### 参照実装の 4 決定（IADR-0379）の写し

| 決定 | 本件での写し |
| --- | --- |
| deny-by-default（呼び出し側の縮退） | BFF は rpc の失敗（全 status・s2s トークン取得失敗）を**「不明」**へ縮退する。一覧は 200 のまま。取り込み側は呼び出し元の ABAC を信じ、渡された ID だけを答える（面に利用者文脈は無い） |
| grpcPort（h2c） | 取り込みは既に `grpcPort: 8081`（helm）・`Grpc__Port`（compose）を持つ（自己申告の面）。**BFF に宛先 `Services__IngestionServiceGrpc` を足す**（helm・compose）。`EastWestGrpcDeploymentWiringTests` の表に経路を足す |
| realm / audience（s2s） | BFF は既存の `bff` の s2s トークン（`ServiceToken:*`）を使う（取り込みの自己申告の面を既に同じトークンで呼んでいる）。取り込みは `AddPlatformAuth` の既定の audience で検証し、面に `ServiceCaller` を掛ける。realm の変更は要らない |
| proto 検査 | `Knowledge.Contracts/Protos/knowledge/ingestion/v1/index_status.proto` を新設し、`scripts/proto-contract-baseline.json` に載せる（`check-proto-contracts.js`） |

## 設計（正は IADR-0565）

### 取り込み側（IngestionService）

- gRPC 面 `knowledge.ingestion.v1.DocumentIndexStatus/GetStatuses`（新しい proto。既存の面へ相乗りしない）。
  - 要求: `repeated string document_ids`（最大 **100**。BFF の 1 ページの上限 `DocumentListWindow.MaxLimit` と同じ）。
    空は空の応答。上限超・UUID でない ID は `INVALID_ARGUMENT`（Qdrant を呼ばない）。重複は 1 つに畳む。
  - 応答: `repeated DocumentIndexState states`（要求の ID ごとに 1 つ・要求の順。`document_id` と `status`）。
  - `status`: `INDEXED` / `EMBEDDING_PENDING` / `NOT_INDEXED`（`UNSPECIFIED` は送らない）。
  - 索引（Qdrant）が引けなければ `UNAVAILABLE`（中身を応答へ載せない）。
  - `ServiceCaller` を要求する。利用者文脈は持たない（ABAC は呼び出し側で済んでいる）。
- 分類（純関数 `DocumentIndexStatusRules`）:
  1. 語彙索引に埋め込み待ちの印（`embedding_pending = true`）の点が 1 つでもある → **埋め込み待ち**（一部のチャンクだけが埋め込めた文書もここ）
  2. それ以外で、ベクトルのコレクションか語彙索引（印なし。高機密の語彙索引だけの文書）に点がある → **索引済み**
  3. どこにも点が無い → **未索引**（取り込み前・取り込みの失敗・露出の除外・アーカイブで外れた文書など）
- 問い合わせ（Qdrant）: コレクションごとに **facet（`document_id`）を 1 回**、フィルタは `document_id ∈ ページの ID`。
  語彙索引は「印あり」「印なし」の 2 回。回数はコレクションの数で決まり、ID の数に依らない。
  facet は `document_id` のキーワード索引を要るので、起動時のブートストラップ（`EnsureKeywordIndexesAsync`）で全コレクションに張る（冪等）。

### BFF

- `GET /bff/documents` の応答 `DocumentListPageDto` に `indexStatuses`（文書 ID → `indexed` / `embedding_pending` / `not_indexed` / `unknown`）を足す。
  **窓の中の文書（`items`）の ID だけ**が鍵になる。窓が空なら照会しない。
- 照会は窓を切り出した**後**（＝ABAC の後）に 1 回。期限は 3 秒。失敗はすべて `unknown`（WARN 1 行。一覧は 200）。
- 宛先 `Services:IngestionServiceGrpc`（未構成なら常に `UNAVAILABLE` → `unknown`）。チャネルはキー付き・`TryAdd`（IADR-0402 決定 6）。

### 画面（SC-05）

- 「本文」の列の後に「索引」の列（`StatusBadge`）: 索引済み（success）・埋め込み待ち（warning）・未索引（neutral）・不明（neutral）。
  応答に鍵が無い文書（旧い BFF）も「不明」。未知の値は生値を出す（`documentStatus.ts` と同じ作法）。
- `indexStatuses` の文言は Lingui のカタログ（ja / en）へ。

## 母集合（規則 9・10）

走査語: `索引の状態` / `文書ごとに引く口` / `文書ごとの状態を引く口` / `DocumentListPageDto` / `Services__.*Grpc`（BFF のブロック）/
`13 経路` / `14 つ目の面`（`.ai-context/specs`・`.ai-context/superpowers`・`CHANGELOG.md`・生成物を除く全追跡ファイル。2026-10-20 に `git grep` で引いた）。

| 該当 | 対応 |
| --- | --- |
| `docs/screens/SC-05_document-management.md` §絞り込み・並べ替え の取り消し線の追記（220–221 行）・§タブ … の「入れていないもの」（253 行） | 索引の状態を入れた旨へ改める。列の節を足す |
| `docs/tests/SC-05_document-management.md` | 試験の行 |
| `docs/api/BFF_bff-surface.md` の `GET /bff/documents` | `indexStatuses` を一言 |
| `docs/api/east-west-grpc.md` | 15 番目の面の節・経路の数 |
| `docs/api/openapi.yaml`・orval 生成物 | `indexStatuses` |
| `IADR-0545`（138 行）・`IADR-0561`（125 行）の残余「索引の状態は出せていない」 | 日付つき追記で IADR-0565 を指す（本文は変えない） |
| `types/documentBody.ts` の注記（「索引の状態ではない」「文書ごとに引く口は無い」） | 後半が誤りになる → 索引の状態は別の列（`types/documentIndexStatus.ts`）と改める |
| `DocumentManagementPage.tsx` の見出しの注記 | 列を足す |
| helm `services.bff`・compose `bff` | 宛先を足す |
| `EastWestGrpcDeploymentWiringTests` | 経路と宛先を足す |
| `scripts/proto-contract-baseline.json` / `scripts/contract-schema-baseline.json` ほか検査器の baseline | 検査器が要求する分 |
| `.ai-context/adr/IADR-0144`・`README.md`・`scripts/*`（「索引の状態列」） | **除外**: IADR の索引表の状態列の話で、本件と無関係 |
| `PrivateNoteIndexProductionTests.cs:255` | **除外**: 語の偶然の一致 |

## 試験

| 層 | 観点 | 期待 |
| --- | --- | --- |
| 取り込み | 分類 | 印ありが 1 点でもあれば埋め込み待ち・印なしの点だけなら索引済み・無ければ未索引 |
| 取り込み | 面 | `ServiceCaller` が付く。上限超・UUID でない ID は `INVALID_ARGUMENT` で store を呼ばない。重複を畳み、要求の順で返す。store の失敗は `UNAVAILABLE` |
| 取り込み | Qdrant（実コンテナ。CI の integration） | 主・語彙索引（印あり / なし）に書いた文書が 3 値に分かれる。ページの外の文書は数えない |
| 取り込み | 索引 | ブートストラップが `document_id` のキーワード索引を全コレクションに張る |
| BFF | 結合 | 窓の文書の ID だけを 1 回で照会し、`indexStatuses` に載せる |
| BFF | 🔴 存在秘匿 | 権限外・他人の個人資料を混ぜた台帳と混ぜない台帳で、応答と照会した ID の集合が一致（陽性対照つき） |
| BFF | 縮退 | 失敗（UNAVAILABLE / INTERNAL / 期限切れ / トークン取得失敗）は全件 `unknown`・200 |
| BFF | 窓が空 | 照会しない |
| 配備 | 配線 | helm・compose の BFF に `Services__IngestionServiceGrpc`（:8081） |
| 画面 | 列 | 4 値の文言・旧い応答（鍵が無い）は「不明」 |

## 検証

- `dotnet build` / `dotnet test`（両ユニット）/ `dotnet format --verify-no-changes`
- `src/` で `pnpm run lint` / `typecheck` / `test` / `format:check`、`pnpm run codegen` / `pnpm run i18n` の再生成で差分なし
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` ほか文書系

## 残余

- 状態は問い合わせた時点の索引の点から導く。取り込みの途中（削除してから書き直すまで）の文書は一瞬「未索引」に見える。
- 状態での絞り込み・並べ替え・件数は入れない（索引は台帳の 1 周の外にあり、全件を照会することになる）。
