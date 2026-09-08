---
title: タグ辞書の読み取り（Graph → Document）を east-west gRPC へ移す（#1255 スライス 7）
type: spec
status: done
related_ids: [FR-18, NFR-09, NFR-16, SC-09, ADR-0029, ADR-0043, ADR-0063, ADR-0075, IADR-0299, IADR-0364, IADR-0379, IADR-0401, IADR-0402, IADR-0410, IADR-0412]
author: Claude（実装）
created: 2026-09-08
updated: 2026-09-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_service-communication.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# 仕様書: タグ辞書の読み取りを east-west gRPC へ移す（#1255 スライス 7）

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-18（AI 提案）／非機能: NFR-09（認可）・NFR-16（east-west の輸送）
- 画面: SC-09（タグ辞書の管理）
- 計画 ADR: **ADR-0029**（サービス間通信）／**ADR-0075**（east-west gRPC の移行順）／
  ADR-0063 決定 2（提案のタグは辞書の値に限る）／ADR-0043 決定 1（辞書を利用者へ丸ごと返さない）
- 実装 ADR: [[IADR-0379]]（前提条件。置き場・versioning・h2c・s2s の 4 決定）／
  [[IADR-0364]] 決定 2（この内部口を置いた当の決定）／[[IADR-0299]] 決定 4（メッシュ内 API の姿勢）／
  [[IADR-0401]] 決定 2（**呼び出し元が要らないものを面へ出さない**）／
  [[IADR-0402]] 決定 6（宛先ごとに 1 チャネル）／[[IADR-0410]]（直前のスライス）／
  [[IADR-0412]]（本 PR で新設。**切り離しの判断**）
- issue: #1255（**閉じない**。残り 5 経路が続く）

## 射程

**Graph → Document の `GET /internal/tags/names` 1 経路だけ**を gRPC へ移す。
**REST は残す**（[[IADR-0379]] 決定 5 のとおり**並走中の正は REST**。切替も戻しも構成だけで行う）。

## 🔴 この経路は前スライスで意図的に見送られていた。その理由がいま解ける

`.ai-context/specs/20260906_issue-1255_knowledge-health-grpc.md` の残 6 箇所の表で、
本経路（#4）は**射程外**とされ、理由はこう書かれていた:

> 同じ名前付きクライアントの兄弟（`HttpDocumentTagWriter`）が**資格情報を運ぶ**。
> 切り離しの判断が要る（§2）

**その判断はいま下せる。** 兄弟の書き込み側は **PR #1322（[[IADR-0410]]）で既に gRPC 客体を持ち**、
利用者文脈は `ADR-0086` 決定 1 に従って**本文で運ぶ**形へ移っている。
読み取り側を gRPC へ移すことは、**共有していた名前つきクライアントを実際に切り離すこと**そのものである。

🔴 **しかもそれは現在の危険を減らす。** 走査で確認したとおり、2 つのアダプタは
同じ client 名 `"DocumentService"` を共有しながら**資格情報の意味論が逆**である ——
`HttpDocumentTagWriter:34-36` は承認者の `Authorization` を**付け**、
`HttpTagDictionaryReader` は**付けない**（付けると一般利用者が 403 になり提案が 0 件になる）。
`IHttpClientFactory` が呼び出しごとに別の `HttpClient` ラッパを返すので**今は汚染しない**が、
**共有インスタンスや型付きクライアントへ寄せる改修が入れば、承認者トークンが匿名の内部口へ漏れる。**

## 母集合（自分で引き直した走査。規則 1〜10）

`git rev-parse --is-shallow-repository` = **`false`**。

| 軸 | 検索語 | 件数 | 内訳 |
| --- | --- | --- | --- |
| 1 | `AddHttpClient(` （非テスト） | **53** | 北南（BFF）・外部（Keycloak / Wiki.js / SaaS / Storage）を含む総数 |
| 2 | `Add<X>GrpcClient(` （`Program.cs`） | **16** | 移行済みの登録 |
| 3 | `Protos/**/*.proto` | **8** | platform 4 / knowledge 4 |
| 4 | `ITagDictionaryReader` | 実装 **1** / 消費者 **1** / 試験替え玉 **3** | 本スライスの射程 |
| 5 | `internal/tags/names` ＋ `NamesPath` | **13 行** | 両側の定数（サービスを跨ぐので共有できない）と試験 |

### east-west の未移行 —— **6 経路**（前スライスの表と同じ数え方で数え直した）

| # | 経路 | 本スライス | 射程外の理由 |
| --- | --- | --- | --- |
| 1 | BFF → Retrieval `POST /search/attribute-values` | — | 兄弟の `/search` が資格情報を運ぶ（[[IADR-0402]] フォローアップ 1） |
| 2 | Document → Notification | — | **realm に client と secret の注入経路が要る**。単独 PR |
| 3 | AiAnalysis → Retrieval | — | Retrieval 宛の proto がまだ無い。#1 と束ねるのが自然 |
| **4** | **Graph → Document `GET /internal/tags/names`** | **✅ 本 PR** | —— |
| 5 | McpServer → 構成で決まる N サービス | — | **扇形**（宛先が `Mcp:Services` で決まる） |
| 6 | `HttpEffectiveConfigCollector` → 全サービス | — | 同上（扇形）。#1255 の作業指示で名指しの射程外 |

**除外（理由つき）**: 軸 1 の 53 件のうち北南（BFF → 各サービス 15）と
外部（Keycloak / Wiki.js / SaaS コネクタ / Storage）は **east-west ではない**ので母集合に入れない。
移行済み 15 経路は **REST と並走中**であり「未移行」ではない。

### 陽性対照

- **PC-1**: 兄弟の `HttpDocumentTagWriter` が走査に現れ「**資格情報を運ぶ**」側へ落ちる ✅
- **PC-2**: 移行済みの `LlmGatewaySuggestionClient` が現れ「移行済み」へ落ちる（gRPC 対が在る）✅
- **PC-3（陰性）**: `ObsidianSyncEndpoints` は**現れない**（受け口の資格情報であって転送ではない）✅

## この経路の性質（実測）

| 観点 | 現状 |
| --- | --- |
| 受け口 | `TagNamesEndpoint`（`MapGet`）。**認証を持たない**（どちらの認可 group にも属さない） |
| 要求 | **引数なし** |
| 応答 | `TagNamesResponse(List<string> Names)`。**名前順**（`OrderBy(t => t.Name)`） |
| 🔴 返さないもの | **使用件数**（`TagDto.UsageCount`）—— 管理側の集計であり、ABAC で絞っていない値を外へ出さない |
| 資格情報 | **運ばない**（読む主体は GraphService 自身。[[IADR-0364]] 決定 2） |
| 消費者 | `AiSuggestionGenerator` **1 つだけ**（生成段で LLM に選ばせる値集合として使う） |
| 🔴 `null` の意味 | **「引けなかった」**。空集合（「辞書が空」）とは**別**。`null` なら**タグ提案を 1 件も作らない** |

## 決定（実装方針）

### 決定 1: **新しい proto を 1 本置く**（既存 2 本へ相乗りしない）

`Protos/knowledge/document/v1/tag_dictionary.proto`（[[IADR-0379]] 決定 1 の置き場）。

- `document_read.proto` へ足さない —— 同 proto は「**書き込み・本文・共有の口はこの面に存在しない**」と
  宣言している。BFF の文書台帳の面に Graph のタグ辞書を混ぜると宣言が嘘になる
- 🔴 `document_tag_write.proto` へ足さない —— **同 proto 自身が
  「一覧・本文・共有・タグ辞書の口はこの面に存在しない」と書いている**。
  加えて `service DocumentTagWrite` は書き込みの面であり、改名は破壊的変更である

**`Knowledge.Contracts.csproj` は触らない** —— `<Protobuf Include="Protos/**/*.proto" …
GrpcServices="Both"/>` の 1 項目が全 proto を拾う（実測）。

### 決定 2: 面は `ServiceCaller` を要求する（REST の匿名口より**狭い**）

[[IADR-0379]] 決定 4 に従う。REST 側は認証を持たないので**権限が狭まる向き**であり、
`document_read.proto` が同じ向きの判断を明記している（`AuthzScope/Resolve` を移したときと同じ）。
**REST の匿名口は残す**（並走の正であり、狭める判断は撤去の段で行う）。

### 決定 3: 🔴 **「引けなかった」と「空」を分ける契約を壊さない**

`ITagDictionaryReader.ReadNamesAsync` の `null` は「引けなかった」、空集合は「辞書が空」である
（`Domain/Ports/ITagDictionaryReader.cs:8-10`）。fail-closed の要であり、
`TagDictionaryEnforcementTests.Unavailable_dictionary_drops_every_tag_but_keeps_links` が固定している。

⇒ gRPC 実装は `RpcException`（`UNAVAILABLE` / `PERMISSION_DENIED` / `UNAUTHENTICATED` ほか）と
s2s トークン取得失敗を**すべて `null`** へ縮退し、**正常応答の空リストは空集合**として返す。
🔴 **新しい枝を作らない**（`document_read.proto` の「無い」と「引けなかった」を分ける作法と同じ）。

### 決定 4: 並走。切替は既存の `Services:DocumentServiceGrpc` 1 本

書き込み側と同じ構成キーで切り替える（**両方が同時に gRPC へ倒れる**）。
宛先が同じなので**チャネルも同じ**（[[IADR-0402]] 決定 6「宛先ごとに 1 チャネル」）——
書き込み側が登録するキー付きチャネル `"DocumentServiceGrpc"` を**共有し、2 本目を作らない**。

★［2026-09-08 追記 / #1255］🔴 **「共有する」だけでは足りなかった。両方の登録を `TryAdd` にした。**
登録関数は面ごとに分かれているので、**読み取り側が登録される順は書き込み側より前にも後にもなり得る**。
片方だけ「既に在れば足さない」にすると、逆順のときに**同じ宛先へ 2 本目**が張られる ——
`GetRequiredKeyedService` は最後の登録を返すので**障害としては現れず、決定だけが静かに破れる**。
⇒ 既存の `AddDocumentTagWriteGrpcClient` の `AddKeyedSingleton` も
`TryAddKeyedSingleton` へ変えた（本 PR の唯一の既存コード変更）。
試験は**登録順を入れ替えた 2 通りを対で**置く（片方だけだと片側の退行を通す。変異 M-3 / M-4 が実証）。

🔴 **deploy / realm / secret は 1 行も変えない。** 実測で次がすべて既に在る:
`Services__DocumentServiceGrpc`（compose `:723` / helm `values.yaml:668`）・
`ServiceToken__ClientId/Secret`（`graph-service`）・DocumentService の `grpcPort: 8081`・
realm の confidential client `graph-service`。

### 決定 5: 定数の二重持ちは**残す**

受け口と読み手が `NamesPath` を別々に持つ形（サービスを跨ぐので共有できない）は REST の話であり、
gRPC では proto が唯一の契約になる。**REST 側の 2 定数は並走のため残す。**

### 決定 6: 🔴 REST と gRPC は**同じ問い合わせ関数**を通る

`TagNamesEndpoint.ReadNamesAsync(db, ct)` を抽出し、REST ハンドラと gRPC サービスの両方がそれを呼ぶ
（**問い合わせを 2 つにしない**。器が JSON か protobuf かだけが輸送ごとに違う）。
写すと、片方だけ順序や射影が変わった状態が作れる —— 本リポジトリが繰り返し踏んでいる形であり、
直近では #1330 が 5 巡かけて潰した（**境界を決める規則が 2 か所にあると必ず割れる**）。

🔴 **名前順は契約である。** 呼び出し元は集合へ落とすので順序を使わないが、面の側で決めておかないと、
後から順序に依存する消費者が現れたときに壊れる。**両面の同値試験を順序込みで置く。**

## テスト（受け入れ基準）

- [x] Given 権限のある s2s 呼び出し / When `ListNames` / Then **名前だけ**が**名前順**で返る
- [x] Given 同上 / When 応答を見る / Then **使用件数を含まない**
- [x] Given 資格情報なし / When `ListNames` / Then `UNAUTHENTICATED`
- [x] 🔴 Given **管理者の利用者トークン** / When `ListNames` / Then `PERMISSION_DENIED`（s2s の門）
- [x] 🔴 Given 面の型 / When 反射で読む / Then `[Authorize(Policy = ServiceCaller)]` が付いている
- [x] Given gRPC 客体 / When 正常応答 / Then 集合が返る（陽性）
- [x] 🔴 Given `UNAVAILABLE` / `PERMISSION_DENIED` / トークン取得失敗 / Then **`null`**（引けなかった）
- [x] 🔴 Given **空リストの正常応答** / Then **空集合**（`null` では**ない**）—— 決定 3 の対
- [x] Given gRPC 客体 / When 呼ぶ / Then `authorization` メタデータに**利用者トークンを載せない**
- [x] 🔴 Given 宛先未設定 / When 拡張を呼ぶ / Then **何も登録しない** ／ 設定済みなら**登録する**（対）
- [x] Given 既存の `TagDictionaryEnforcementTests` 6 本 / When 回す / Then **緑のまま**（下流を動かさない）
- [x] 🔴 ★追加。Given 読み取りと書き込みの登録 / When **どちらを先に登録しても** / Then
      同じ宛先のチャネルは **1 本**（決定 4 の追記。登録順を入れ替えた 2 通りを対で置く）
- [x] 🔴 ★追加。REST と gRPC が**順序込みで同値**（決定 6 ＝ 同じ問い合わせ関数を通ることの観測）
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が knowledge 全体で通る
- [x] `node scripts/check-proto-contracts.js` が baseline 更新後に緑

**試験数**（いずれも実出力。**差分は数え直した値ではなく、新規ファイルを名指しで走らせた実測である**）:
GraphService.Tests **540**（うち新規 `GrpcTagDictionaryReaderTests` が **15**）／
DocumentService.Tests **441**（うち新規 `GrpcTagDictionaryTests` が **8**）。
knowledge 全体で**失敗 0・スキップの増減なし**（`Knowledge.IntegrationTests` のスキップ 44 は不変）。

## 変異試験（実出力。すべてビルドし直して実走し、着地は `grep` で確認した）

| # | 変異 | 位置 | 赤になった試験 |
| --- | --- | --- | --- |
| M-1 | `RpcException` の縮退を `null` → 空集合 | `GrpcTagDictionaryReader` | **5**（`輸送の失敗は引けなかったへ倒す` の 5 例。合計 15 中 10 合格） |
| M-2 | 正常応答の空リストを空集合 → `null` | 同上 | **1**（`空の応答は空集合であって引けなかったではない`） |
| M-3 | 書き込み側の登録を `TryAdd` → `Add` | `GrpcDocumentTagWriter` | **1**（`同じ宛先へチャネルを2本張らない`） |
| M-4 | 読み取り側の登録を `TryAdd` → `Add` | `GrpcTagDictionaryReader` | **1**（同上） |
| M-5 | 面から `[Authorize(ServiceCaller)]` を外す | `TagDictionaryGrpcService` | **3**（未認証 / 管理者トークン / 反射の門） |
| M-6 | `ReadNamesAsync` から `OrderBy` を外す | `TagNamesEndpoint` | **1**（`ListNames_is_ordered_by_name`） |
| M-7 | `MapGrpcService<TagDictionaryGrpcService>()` を外す | `DocumentService/Program.cs` | **6**（面が丸ごと死ぬ） |

🔴 **M-1 と M-2 は別々の試験しか殺さない。** これが決定 3 の「両方向を対で置く」の根拠である ——
片方だけの試験では、もう一方の向きへ壊れたまま緑になる。
🔴 **M-3 と M-4 が同じ 1 本を殺すのは、その試験が登録順の 2 通りを持っているからである。**
1 通りしか置かなければ、どちらか一方の変異は生存する。
🔴 変異はすべて戻し、**戻したことは「試験が緑に戻った」で確かめた**
（`git diff` では確かめない —— #1324 で `copyFileSync` の mtime 保存に嵌った）。

## やらないこと

- **REST の口を消すこと**（並走の正。撤去は全経路が安定した段で反転の IADR を起こしてから）
- **REST の匿名口へ認証を足すこと**（[[IADR-0364]] 決定 2 の姿勢を本 PR で変えない）
- **使用件数を面へ出すこと**（ADR-0043 決定 1 / [[IADR-0364]] 決定 2）
- **`TagNamesResponse` を触ること**（`contract-schema-baseline.json` が凍結している。並走のため不変）
- **deploy / realm / secret / csproj を触ること**（既に揃っている）
- **#1255 を閉じること**（残り 5 経路が続く）
