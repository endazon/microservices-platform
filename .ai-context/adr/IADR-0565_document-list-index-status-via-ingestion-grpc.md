---
title: IADR-0565 文書管理の一覧に索引の状態（索引済み・埋め込み待ち・未索引、引けなければ不明）を出す。取り込み側に文書 ID の集合から状態を返す east-west gRPC 面（DocumentIndexStatus）を置き、BFF は ABAC で切り出した窓の ID だけを 1 回で問い合わせる
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-02, SC-05, UC-03, NFR-09, NFR-16, ADR-0029, ADR-0075, ADR-0127, IADR-0009, IADR-0041, IADR-0045, IADR-0379, IADR-0401, IADR-0402, IADR-0412, IADR-0533, IADR-0536, IADR-0537, IADR-0545, IADR-0561]
author: claude
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（文書管理画面「一覧の送り方と件数」）
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md（east-west は gRPC）
related_specs:
  - ../specs/20261020_1993_document-list-index-status.md
---

# IADR-0565: 文書管理の一覧の索引の状態は、取り込み側の gRPC 面から窓の ID だけを 1 回で引く（#1993）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-20
- 決定者: claude（#1923 のオーナーの依頼 5「列の見直し」の残り）

## 起点・関連

- 起点 issue: [#1993](https://github.com/endazon/microservices-platform/issues/1993)（#1923 の依頼 5 のうち、#1990・#1992 で残った「索引の状態」の列）。
- 関連する計画書 ID: FR-06・SC-05・UC-03。計画 §文書管理画面「一覧の送り方と件数」は一覧の要素と「件数は ABAC で絞った後の数」を定めるが、
  **列の一覧も索引の状態の分類も定めていない**（実測 2026-10-20）。本決定は実装の判断であり、計画へ写さない（IADR-0561 と同じ扱い）。
- 関連する計画 ADR: ADR-0029 / ADR-0075（east-west は gRPC）・ADR-0127（高機密は語彙索引だけ）。
- 守る判断: [IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md)（存在秘匿）・IADR-0041 / IADR-0045（ABAC の実施点は BFF）・
  [IADR-0379](./IADR-0379_east-west-grpc-preconditions.md)（proto の置き場・h2c・s2s・deny-by-default）・
  [IADR-0401](./IADR-0401_east-west-grpc-authz-user-directory.md) 決定 2・5（要らないものを面へ出さない・「無い」と「引けなかった」を分ける）・
  [IADR-0402](./IADR-0402_east-west-grpc-bff-document-read.md) 決定 6（宛先ごとのキー付きチャネル）・[IADR-0533](./IADR-0533_east-west-rest-retirement.md)（REST の並走を作らない）・
  [IADR-0536](./IADR-0536_embedding-pending-lexical-points.md)（埋め込み待ちの点）・[IADR-0537](./IADR-0537_document-list-pagination-offset-window-after-abac.md)（窓は ABAC の後）。
- 作業仕様書: [20261020_1993_document-list-index-status](../specs/20261020_1993_document-list-index-status.md)。
- 採番: 着手時は 0563 としたが、push の直前に引き直すと `origin/develop` の最大は IADR-0563（#2001）、開いている PR #2002 が 0564 を採っていた（2026-10-20）。本件は **0565**。

## コンテキストと課題

- 索引の状態は取り込み側（IngestionService）が検索の索引（Qdrant）の点として持つ。点はベクトルのコレクション（モデル別）と語彙索引（ベクトルを持たない 1 本）に分かれ、
  語彙索引には高機密の文書（ADR-0127。印なし）と、埋め込みが使えなかった public / internal の文書（IADR-0536。`embedding_pending = true`）が載る。
- 文書 ID から状態を引く口は、取り込み側にも BFF にも無かった（IADR-0545・IADR-0561 の残余）。
- 決めること: (1) 状態の分類、(2) 口の置き場と形、(3) 権限（ABAC）、(4) 一覧への結合と縮退、(5) 照会の費用（N+1 にしない）。

## 検討した選択肢

### 口の置き場

1. **取り込み側（IngestionService）に新しい gRPC 面を置く（採用）。** 点を書く側であり、コレクションの構成（モデル別・語彙索引・印のキー）を知っている。
   h2c リスナ・`grpcPort`・認証（`AddPlatformAuth`）は自己申告の面で既に在る。
2. 検索側（RetrievalService）に置く（棄却）。検索は主のコレクションと語彙索引を読むが、埋め込み待ちの印の意味（書き手の都合）を知らない。
   印のキーと「印なし＝高機密」という書き手の約束を 2 つのサービスへ写すことになる。
3. 台帳（DocumentService）に索引の状態の列を持たせ、取り込みの完了で書き戻す（棄却）。既存の約 4 万件へ遡及する移行が要り、
   埋め込み直し（IADR-0536）・削除・露出の切り替えのたびに書き戻しが要る。事実（点の有無）を 2 か所に持つことになる。

### 問い合わせの形

1. **コレクションごとに `document_id` の facet を 1 回（採用）。** フィルタは `document_id ∈ 窓の ID`。語彙索引は印あり・印なしの 2 回。
   回数はコレクションの数（既定 1 ＋ 語彙索引 2 ＝ 3 回）で決まり、窓の文書の数に依らない。facet は値の有無だけを使い、数は捨てる。
2. 文書ごとに `Count`（棄却）。窓 100 件 × コレクション数の往復になる（N+1）。
3. `Scroll` で点を読み、文書 ID を集める（棄却）。1 文書のチャンクの数に比例して点を読む（大きな文書で数千点）。

## 決定

**決定 1: 状態は 3 値（取り込み側）と、BFF の「不明」の 4 つを出す。**

| 値 | 条件（取り込み側。`DocumentIndexStatusRules.Classify`） |
| --- | --- |
| 埋め込み待ち（`embedding_pending`） | 語彙索引に埋め込み待ちの印の点が 1 つでもある。🔴 **一部のチャンクだけが埋め込めた文書もここ**（意味検索には一部しか出ないので「索引済み」と言わない） |
| 索引済み（`indexed`） | それ以外で、ベクトルのコレクションか語彙索引（印なし）に点がある。高機密の語彙索引だけの文書（ADR-0127）もここ —— 方針どおりに索引されている |
| 未索引（`not_indexed`） | どのコレクションにも点が無い（取り込み前・取り込みの失敗・露出の 3 属性の除外・アーカイブで外れた文書など） |
| 不明（`unknown`。BFF だけが出す） | 取り込み側から引けなかった。応答に鍵の無い文書（旧い BFF）も画面は「不明」と読む |

- 🔴 **「不明」を「未索引」へ畳まない。** 畳むと、索引の障害が「全部未索引」に見える。
- 画面の色: 索引済み＝success、埋め込み待ち＝warning（全文では引けるが意味検索に出ない。気付かせたい）、未索引・不明＝neutral（未索引は異常とは限らない）。
  どれも `StatusBadge`（tone ごとの固定アイコン＋文言）で、色だけで意味を持たせない。

**決定 2: 取り込み側の面 `knowledge.ingestion.v1.DocumentIndexStatus/GetStatuses`（新しい proto `index_status.proto`）。**

- 要求は `repeated string document_ids`（UUID）。**上限 100**（BFF の窓の上限 `DocumentListWindow.MaxLimit` と同じ）。超過・UUID でない ID は `INVALID_ARGUMENT`
  （切り詰めない —— 切ると呼び出し元が残りを「未索引」と読み違える）。0 件は空の応答。重複は最初の位置に 1 つ。
- 応答は要求の順に `DocumentIndexState`（`document_id`・`status`）。`UNSPECIFIED` は送らない。
- 索引（Qdrant）が引けなければ `UNAVAILABLE`（詳細は status に載せない。WARN 1 行）。語彙索引のコレクションが無い（`NotFound`。ブートストラップの失敗）は
  「そこに点が無い」として読む（`DeleteByDocumentFromAllAsync` と同じ扱い）。
- 問い合わせは上の「コレクションごとの facet」。facet は `document_id` のキーワード索引を要るので、起動時のブートストラップ（`EnsureDocumentIdIndexAsync`）が
  全コレクション（モデル別 ＋ 語彙索引）に張る（冪等。`EnsureKeywordIndexesAsync` の直後）。既存のコレクションも起動のたびに収束する。
- 既存の proto（自己申告）へ相乗りしない（面の名が体を表さなくなる。IADR-0401 決定 2 の作法）。

**決定 3: 権限（ABAC）は呼び出し側（BFF）で済ませ、面は利用者の文脈を持たない。**

- 面は `ServiceCaller`（`platform-service`）を要求する。利用者のトークンでは通らない。要求に `user_id` も属性も無い（要らないものを面へ出さない）。
- 🔴 **BFF は窓を切り出した後（＝ABAC と個人資料の除外の後）に、窓の中の文書の ID だけを渡す。** 権限外の文書の ID は取り込み側へ出ない（IADR-0009）。
  面が返すのは渡された ID の状態だけで、表題・属性・件数・点の中身は返さない。
- 取り込み側で呼び出し元を BFF だけに絞る許可集合（検索の `TrustedUserContextClients` のような）は置かない。面は利用者の文脈で動かず、返すのは
  「渡した ID の点の有無」だけであり、`ServiceCaller` を持つ主体（メッシュ内部のサービス）へ新しく漏れるものが無い。

**決定 4: BFF の結合と縮退。**

- `GET /bff/documents` の応答 `DocumentListPageDto` に `indexStatuses`（文書 ID → 決定 1 の値）を末尾追加する（既定は空。旧い応答は全件「不明」として読める）。
  鍵は `items` の ID だけ。窓が空・許可ポリシーが無いときは照会しない。
- 照会は 1 要求につき 1 回（窓は最大 100 件＝面の上限）。期限 3 秒。
- 🔴 **失敗（rpc の全 status・期限切れ・s2s トークン取得失敗）はすべて全件「不明」へ縮退し、一覧は 200 のまま。** 参照実装（IADR-0379 決定 4）の
  deny-by-default と同じ向きの縮退であり、一覧の本体（台帳の 1 周）は索引の障害に巻き込まない。WARN 1 行（`DocumentReadFailureLog`）。
- 宛先 `Services:IngestionServiceGrpc`（未構成なら常に `UNAVAILABLE` → 「不明」。IADR-0533 決定 2）。チャネルはキー付き・`TryAdd`（IADR-0402 決定 6 / IADR-0412 決定 5）。
  資格情報は既存の `bff` の s2s トークン（取り込みの自己申告の面を既に同じトークンで呼んでおり、realm・audience の変更は要らない）。
- 配備: helm `services.bff.extraEnv` と compose `bff` に `Services__IngestionServiceGrpc: http://ingestion-service:8081`。取り込み側の `grpcPort` は既に在る。

## 理由

- 点を書く側が状態を答えるので、コレクションの構成と印の意味が 1 か所に閉じる。台帳への書き戻しも移行も要らない。
- 問い合わせの回数がコレクションの数で決まり、窓の文書の数・1 文書のチャンクの数のどちらにも依らない。
- ABAC の実施点（BFF）と窓の順序（IADR-0537）を動かさずに、窓の後ろへ 1 回の照会を足すだけで済む。存在秘匿の論証は「窓の ID しか渡さない」の 1 本である。

## 結果

- 試験: 取り込み `DocumentIndexStatusGrpcServiceTests`（分類・上限・UUID・重複と順序・UNAVAILABLE・本番の面が ServiceCaller の後ろ）・
  `QdrantIndexPresenceTests`（回数が ID の数に依らない・印の分け方・値の読み方・NotFound・`document_id` の索引）・`QdrantKeywordIndexHostedServiceTests`（ブートストラップの順）、
  実 Qdrant `IndexStatusQdrantTests`（CI の integration）、BFF `BffDocumentListIndexStatusTests`（窓の ID を 1 回・存在秘匿〔陽性対照つき〕・縮退 5 種・未指定は不明・登録）、
  配線 `EastWestGrpcDeploymentWiringTests`（経路 I-1）、画面 `documentIndexStatus.test.ts`・`DocumentManagementPage.test.tsx`・e2e の SC-05 スモーク。
  変異で確かめた: BFF の縮退の catch を外すと BFF の試験が 6 件赤、分類の順（埋め込み待ちが先）を入れ替えると取り込みの試験が赤。
- 配備の順序: 新しい BFF と旧い取り込みの組では面が無い（`UNIMPLEMENTED`）→ 全件「不明」。新しい画面と旧い BFF の組でも全件「不明」。一覧はどの組でも出る。
  取り込み側は再起動で `document_id` の索引を張る（既存の点に対して Qdrant が構築する）。移行（DB）は無い。

## 残余リスク

- 状態は照会した時点の点から導く。取り込みの途中（全コレクションから消してから書き直すまで）の文書は一瞬「未索引」に見える。
- 状態での絞り込み・並べ替え・件数は入れない。索引は台帳の 1 周（IADR-0537）の外にあり、入れると全件の照会が要る。
- 起動時の `document_id` の索引の作成は、大きなコレクションでは構築を待つ（`wait` 既定）。失敗はブートストラップの Error に残り、そのあいだ照会は facet の失敗 → 「不明」になる。
- 露出の除外・アーカイブで索引から外れた文書と、取り込みに失敗した文書は、どちらも「未索引」で区別しない（理由は取り込み側の点からは分からない）。
