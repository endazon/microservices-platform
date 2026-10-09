---
title: IADR-0529 露出の 3 属性を 3 つとも除外にした組織文書は Wiki 同期と MCP の文書一覧からも外し、組織文書への適用を各消費面の試験で固定する
type: impl-adr
status: Accepted
related_ids:
  - FR-13
  - FR-16
  - FR-19
  - UC-07
  - UC-08
  - ADR-0034
  - ADR-0046
  - ADR-0061
  - IADR-0020
  - IADR-0021
  - IADR-0396
  - IADR-0455
  - IADR-0483
  - IADR-0512
author: claude
created: 2026-10-10
updated: 2026-10-10
---

# IADR-0529: 露出の 3 属性を 3 つとも除外にした組織文書は Wiki 同期と MCP の文書一覧からも外し、組織文書への適用を各消費面の試験で固定する

- 状態: Accepted
- 日付: 2026-10-10
- 決定者: 実装エージェント（worker）／#1879（planning#784 の利用者裁定 2026-10-10 を受けて）

## 背景

planning#784 の利用者裁定（2026-10-10）: AST の承認待ちの報告書（ドラフト）を、知識ユニットの**組織文書**として保存する。
露出の 3 属性（`search_exposure` / `graph_exposure` / `ai_input`。FR-19・計画 ADR-0061）は 3 つとも `excluded` とする。

- SC-03 では閲覧できる（機密区分 internal・ABAC に従う）
- 索引・検索・RAG・グラフ・MCP・Wiki には載せない
- 確定したら AST が確定版を作成し、ドラフトを削除する

`origin/develop` `d41e8641` で実測した現状:

| 消費面 | 現状 | 根拠 |
| --- | --- | --- |
| 発行の門 | 組織文書は常に発行する（`PassesPublishGate`） | IADR-0455 決定 2。これで下流へ撤収・アーカイブが届く |
| 取り込み | `IsIndexable` が偽ならチャンクを作らず、既存を全索引から削除 | `DocumentUpdatedConsumer` |
| 検索の出口（REST・MCP の `retrieval.search_documents`・RAG の候補） | 用途の露出キーで落とす | IADR-0512 決定 1 |
| RAG の文脈の選別 | `AiInputExposure.IsAllowed` で落とす | IADR-0283 決定 3 |
| グラフの同期・閲覧（MCP の `graph.*` を含む） | `IsGraphAllowed` が偽ならノードを作らず撤収、閲覧でも落とす | IADR-0396 決定 4・5 |
| **Wiki 同期** | **露出を見ずに Wiki.js へ載せる** | `DocumentSyncConsumer` |
| **MCP の文書一覧（`document.list_documents`）** | **露出を見ずに ABAC の範囲を全部返す** | IADR-0483 |

`DocumentExposure.IsAllowed` は明示の値を文書種別より優先するので、3 つとも `excluded` の組織文書は
`IsIndexable` が偽になる。取り込み・検索・RAG・グラフはこの判定で既に外れる。外れていないのは Wiki と MCP の文書一覧の 2 つである。
Wiki.js は本文の実体を持ち、Wiki の検索（`SearchPages`）は Wiki.js の全文検索を引く。このままではドラフトが SC-04 と Wiki の検索に出る。

## 決定

### 決定 1: Wiki 同期の述語は `DocumentExposure.IsWikiPublishable` に置き、索引の門と同じ粒度にする

`IsWikiPublishable(attrs) = !DocumentScopes.IsPrivateNote(attrs) && DocumentExposure.IsIndexable(attrs)`。

- 個人資料は露出によらず偽（計画 ADR-0046 D-01）。
- 組織文書は `IsIndexable` に従う。3 つとも `excluded` なら偽、露出キーが無ければ真（既存の組織文書は変わらない）。
- **軸を 1 つ（横断検索）に絞らない。** 裁定が述べたのは「3 つとも除外」の組だけである。一部だけ除外した組織文書の Wiki の扱いは計画が述べていない。
  索引の門（ADR-0061 決定 1・2「1 つでも ON なら載せる」）と同じ粒度に揃え、条件は書き下さずに `IsIndexable` から導く。

述語は `Knowledge.Contracts` の `DocumentExposure` に置く（判定の単一情報源。IADR-0396 決定 1 の作法）。

### 決定 2: 含める → 除外の切り替えは、削除の伝播と同じ 2 手でページを撤去する

`DocumentSyncConsumer` は、個人資料の分岐の**後**、メタデータの upsert の**前**に門を置く。門で偽なら次の 2 手を行う。

1. Wiki.js の実体を削除する（正準パス `doc/<DocumentId>`。未存在は成功扱い。期限は `WikiSyncTimeouts.WikiJs`）
2. 同期メタデータの行を削除する（ゲートウェイの一覧・個別・検索から不可視）

これは削除の伝播（`DocumentDeletedConsumer`）と同じ手順である。メタデータが無くても Wiki.js 側の削除は試みる（冪等・deny-closed）。
除外 → 含めるへ戻すと、通常の経路でページを作り直す。

- **アーカイブ（非公開化）にしない。** 本文の実体が Wiki.js に残り、Wiki.js 側の設定 1 つで検索に戻る。ADR-0061 決定 4「ON → OFF は削除まで及ぶ」と同じ向きにする。
- **個人資料の分岐は門より前に置いたままにする。** 個人資料は「組織文書から個人資料へ変わっても既存ページを残す」二層目の挙動を固定している（#449・IADR-0278）。門へ合流させると撤去が個人資料にも及ぶ。

### 決定 3（MCP）: 一覧（`document.list_documents`）から外し、ID 指定の取得（`document.get_document`）は SC-03 と同じく残す

MCP のツール 6 本を実測で分類した。

| ツール | 3 つとも除外の組織文書 | 理由 |
| --- | --- | --- |
| `retrieval.search_documents` | 返らない（変更なし） | 検索の出口が `search_exposure` で落とす。取り込み側でチャンクも作られない |
| `graph.get_backlinks` / `graph.get_links` / `graph.traverse` | 返らない（変更なし） | ノードが作られない。閲覧側も `IsGraphAllowed` で落とす |
| **`document.list_documents`** | **返らない（本決定で変更）** | `DocumentExposure.IsMcpListable` で、判定点の後・件数を数える前に落とす。`total_count` にも入れない |
| `document.get_document` | ABAC の範囲で返る（変更なし） | 下記 |

- **一覧を閉じる理由**: 一覧は外部の AI エージェントが文書を見つける列挙の経路である。裁定「MCP に載せない」はこの経路に当たる。
- **個別の取得を残す理由**: 文書 ID を指定して ABAC の範囲で題名と属性を読むのは、SC-03 の閲覧（REST `GET /documents/{id}`）と同じ意味である。
  本文は MCP の応答に載らない（題名と許可リストの属性だけ）。その ID は MCP の一覧・検索・グラフのどれからも得られない。
  個別まで閉じると、REST と MCP の個別の取得が一致する性質（X-59）を崩すわりに、閉じる経路が増えない。
- **個人資料は `IsMcpListable` で落とさない（常に真）。** 個人資料の MCP での扱いは計画 ADR-0034 決定 9（サービスアカウント実行では返さない）が別に定めている。
  露出トグルを MCP の一覧へ効かせることは計画が裁定していない。個人資料まで落とすと、既定（3 つとも OFF）の資料が利用者自身の MCP の一覧から一斉に消える。

### 決定 4: 組織文書への適用を、各消費面の試験で固定する

3 つとも `excluded` の組織文書について、各面の試験に否定形と陽性対照（露出キーの無い組織文書・含めるへ戻した組織文書）を対で置く。

| 面 | 試験 |
| --- | --- |
| 判定 | `DocumentExposureTests`（`IsIndexable` / `IsWikiPublishable` / `IsMcpListable`） |
| 取り込み | `PrivateNoteIndexProductionTests`（載らない・含める → 除外で削除・除外 → 含めるで再び載る） |
| 検索の出口（横断検索・AI 入力の用途） | `OrganizationDocumentExposureTests`（新規） |
| RAG の文脈 | `RagContextAiInputExclusionTests` |
| グラフの同期 | `GraphDocumentSyncConsumerTests`（ノードを作らない・撤収・作り直し） |
| Wiki 同期 | `DocumentSyncConsumerTests`（載せない・撤去・戻せば載る・一部除外は載る・個人資料は撤去しない） |
| MCP の文書一覧 | `GrpcMcpToolExecutionTests`（X-70） |

「組織文書は常に true（露出キーを持たない）」と書いたコメントは「露出キーを持たない組織文書は true」へ直し、明示した組織文書が落ちることを書き足す。
作成時の検証は、組織文書に付いた露出キーを従来どおり受け付ける（`CreateDocumentValidator`。拒否すると AST がドラフトを保存できない）。
発行の門（IADR-0455 決定 2）は変えない。組織文書は常に発行するので、撤収とページの撤去が下流へ届く。

## 検討した代替案

| # | 案 | 採らなかった理由 |
| --- | --- | --- |
| 1-A | Wiki の門を `IsSearchAllowed`（横断検索の軸だけ）にする | 一部除外の扱いを実装で決めることになる。裁定は 3 つとも除外の組だけを述べた |
| 1-B | WikiService の中に条件を書き下す | 判定が 2 か所に割れ、軸を足したときに片方だけ古くなる（IADR-0396 決定 1 が塞いだ型） |
| 2-A | 除外へ切り替えたらアーカイブ（unpublish + private）にする | 本文の実体が Wiki.js に残り、設定 1 つで検索へ戻る |
| 2-B | 撤去を個人資料にも及ぼす | #449 の二層目（上流が破れたときに証拠を消さない）を壊す |
| 3-A | MCP の個別の取得も閉じる | SC-03 と意味がずれ、X-59 の一致を崩す。本文は元から載らず、閉じる経路も増えない |
| 3-B | MCP の一覧で個人資料にも露出を効かせる | 裁定の無い挙動の変更。既定 OFF の個人資料が利用者自身の一覧から消える |
| 3-C | 発行の門で 3 つとも除外の組織文書を止める | IADR-0455 で退けた形。撤収・アーカイブが下流へ届かなくなる |

## 結果

- 3 つとも `excluded` の組織文書は、索引・検索・RAG・グラフ・MCP の検索とグラフ・MCP の文書一覧・Wiki のどれにも載らない。SC-03 と MCP の ID 指定の取得では、ABAC の範囲で読める。
- 露出キーを持たない既存の組織文書の挙動は変わらない（各試験の陽性対照）。
- 3 つとも除外の組織文書が更新されるたびに、Wiki.js の削除（未存在は成功扱い）が 1 回走る。AST のドラフトは件数が少ないので、負荷は問題にならない。

## 残余リスク・未決

- **計画への反映は planning#784 で裁定待ち**（MSP FR-19・ADR-0061 の射程を「露出 3 属性は組織文書にも効く」へ広げること）。本 IADR は裁定済みの内容（2026-10-10）を先に実装した。計画の文言が別の形で確定したら追随する。
- **一部だけ除外した組織文書**の Wiki・MCP の一覧の扱いは計画が述べていない。決定 1・3 は索引の門と同じ粒度（1 つでも含めれば載せる）とした。
- **配備順**: AST がドラフトの保存を有効にするのは、本変更が配備された後である（planning#784 の配備順の制約）。
- 既に Wiki.js に載った組織文書を後から除外にした場合、ページの撤去は次の `DocumentUpdated`（除外へ切り替えた更新そのもの）で起きる。その更新が届くまでの間はページが残る。
- 稼働クラスタに「露出キーを持つ組織文書」が実在するかは測っていない（本作業ではクラスタに触れない）。
