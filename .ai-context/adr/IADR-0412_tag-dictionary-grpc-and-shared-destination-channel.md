---
title: IADR-0412 タグ辞書の読み取りを 3 本目の proto として分け、同じ宛先へ 2 本目のチャネルを張らせない
type: impl-adr
status: Accepted
related_ids: [FR-18, NFR-09, NFR-16, SC-05, SC-09, ADR-0029, ADR-0043, ADR-0063, ADR-0075, IADR-0299, IADR-0364, IADR-0379, IADR-0401, IADR-0402, IADR-0410]
author: claude
created: 2026-09-08
updated: 2026-09-08
---

# IADR-0412: タグ辞書読み取りの east-west gRPC 化と、宛先ごと 1 本のチャネル

## 状況

`ADR-0029` / `ADR-0075` は east-west の同期呼び出しを gRPC へ移すと定め、#1255 がその展開を負う。
2026-09-08 時点で gRPC 面を持つのは 7 経路であり、**REST のみで残っている east-west は 6 経路**である。
本 IADR はそのうち**最小の一枚** —— GraphService → DocumentService の
`GET /internal/tags/names`（タグ辞書の名前集合の読み取り）を扱う。

### 🔴 この経路は #1321 で意図的に見送られていた

見送りの理由は「同じ名前つき HTTP クライアント `"DocumentService"` を共有する兄弟
（`HttpDocumentTagWriter`）が**利用者の資格情報を運ぶ**ので、切り離しの判断が要る」だった。

**その判断はいま下せる。** 兄弟の書き込み側は PR #1322（[[IADR-0410]]）で既に gRPC 客体を持ち、
利用者文脈は計画 `ADR-0086` に従って**本文で運ぶ**形へ変わっている。読み取り側を gRPC へ移すことは、
**共有していた名前つきクライアントを実際に切り離すこと**そのものである。

しかもそれは**現在の危険を 1 つ減らす**。走査で確かめたとおり、両者は同じ client 名を共有しながら
**資格情報の意味論が逆**である ——

| | 資格情報 | 呼び出し先 |
| --- | --- | --- |
| `HttpDocumentTagWriter` | 承認者本人の `Authorization` を転送する | `POST /documents/{id}/tags`（認可あり） |
| `HttpTagDictionaryReader` | **何も付けない** | `GET /internal/tags/names`（**認証を持たない**内部口。[[IADR-0364]] 決定 2） |

`IHttpClientFactory` は名前ごとにハンドラを共有するだけで既定ヘッダは共有しないため、現時点で
漏れてはいない。しかし**両者を 1 つの型付きクライアントへまとめる**ような素直な整理が入れば、
承認者のトークンが**認証を持たない内部口**へ乗る。共有をコードから消すのが確実な直し方である。

## 決定

### 決定 1: **新しい proto を 1 本置く**（既存 2 本へ相乗りしない）

`Knowledge.Contracts/Protos/knowledge/document/v1/tag_dictionary.proto`（[[IADR-0379]] 決定 1）。
DocumentService 宛としては **3 本目**の面である。

- `document_read.proto` へ足さない —— 同 proto は「書き込み・本文・共有の口はこの面に存在しない」と
  宣言している。あれは **BFF の文書台帳の面**であり、Graph のタグ辞書を混ぜると宣言が嘘になる。
- `document_tag_write.proto` へ足さない —— 同 proto は「一覧・本文・共有・**タグ辞書**の口はこの面に
  存在しない」と**自ら書いている**。加えて `service DocumentTagWrite` は書き込みの面であり、
  読み取り rpc を足すと名が体を表さなくなる（改名は破壊的変更である）。

いずれも [[IADR-0401]] 決定 2 の作法 —— **呼び出し元が要らないものを面へ出さない**。
「面が増えるコスト」より「宣言が嘘になるコスト」を重く見る。宣言が嘘になると、
**次に面を読む者が宣言を根拠に判断できなくなる**（宣言は読む者のための機械ではない不変条件である）。

面の中身は極小である。要求は**空**、応答は**名前の配列だけ**。`user_id` も `user_attributes` も無い
—— 読む主体は GraphService 自身であり（[[IADR-0364]] 決定 2）、辞書は**提案の生成段で LLM に
選ばせる値集合**として引くもので、利用者へ返るものではない。使用件数（`TagDto.UsageCount`）も
出さない —— 管理面の集計であり生成に要らず、ABAC で絞っていない値を外へ出すことになる。

### 決定 2: 面は `ServiceCaller` を要求する（REST の匿名口より**狭い**）

REST の受け口は**認証を持たない**（[[IADR-0364]] 決定 2。[[IADR-0299]] 決定 4 の
`/internal/knowledge-health/observations` と同じ「メッシュ内部 API」の姿勢）。
gRPC 面は [[IADR-0379]] 決定 4 に従い `ServiceCaller`（realm ロール `platform-service`）を掛ける。

🔴 **権限が狭まる向き**である。`document_read.proto` が同じ向きの判断を明記しており、揃える。
🔴 **REST の匿名口は残す** —— 並走中の正は REST であり（[[IADR-0379]] 決定 5）、
狭める判断は REST を撤去する段で行う。

### 決定 3: 🔴 **「引けなかった」と「空」を分ける契約を壊さない**

`ITagDictionaryReader.ReadNamesAsync` の **`null` は「引けなかった」**、**空集合は「辞書が空」**である。
これは fail-closed の要であり、`TagDictionaryEnforcementTests` の
`Unavailable_dictionary_drops_every_tag_but_keeps_links` が固定している。

gRPC 実装は `RpcException`（`UNAVAILABLE` / `PERMISSION_DENIED` / `UNAUTHENTICATED` ほか）と
s2s トークン取得失敗を**すべて `null`** へ縮退し、**正常応答の空リストは空集合**として返す。
**新しい枝を作らない。**

🔴 **この不変条件は片方向では固定できない。** 実測（下記 変異 M-1 / M-2）のとおり、
「`null` を空集合へ倒す」変異と「空集合を `null` へ倒す」変異は**別々の試験しか殺さない** ——
どちらか一方だけの試験では、もう一方の向きへ壊れたまま緑になる。**両方向を対で置く。**

### 決定 4: 切替は既存の `Services:DocumentServiceGrpc` **1 本**である

書き込み側と**同じ構成キー**で切り替える（両方が同時に gRPC へ倒れる）。

🔴 **1 つの宛先を 2 つの鍵で切り替えない。** 鍵を分けると「読みは gRPC・書きは REST」という
中間状態が構成だけで作れてしまい、**再現しない不具合の温床**になる。宛先は 1 つなのだから鍵も 1 つである。

deploy は 1 行も変えない —— `Services__DocumentServiceGrpc` は compose・helm とも graph-service へ
既に設定済みで、`ServiceToken__ClientId/Secret`（`graph-service`）と DocumentService の
`grpcPort` も既に在る（実測済み）。

### 決定 5: 🔴 **同じ宛先へ 2 本目のチャネルを張らせない**（[[IADR-0402]] 決定 6）

GraphService は**同じ呼び出し先へ 2 つ目の gRPC クライアント**を持つ最初のサービスである。
登録関数は面ごとに分かれている（`AddDocumentTagWriteGrpcClient` / `AddTagDictionaryGrpcClient`）ため、
**両方がチャネルを登録しようとする**。

**両方を `TryAddKeyedSingleton` にする**（片方だけでは足りない）。

- `Add` のままだと、**登録順しだいで同じ宛先へ 2 本**張られる。
- 🔴 **障害としては現れない。** `GetRequiredKeyedService` は最後の登録を返すので、
  どちらのクライアントも同じチャネルを引く。**規約だけが静かに破れる。**
- したがって試験は**登録順を入れ替えた 2 通り**を対で置く —— 片方だけだと、
  片側が `TryAdd` を失っても緑のままになる（実測: M-3 / M-4 はそれぞれ逆順でしか捕まらない）。

### 決定 6: REST と gRPC は**同じ問い合わせ関数**を通る

`TagNamesEndpoint.ReadNamesAsync(db, ct)` を抽出し、REST ハンドラと gRPC サービスの両方がそれを呼ぶ。
写すと、片方だけ順序や射影が変わった状態が作れる —— 本リポジトリが繰り返し踏んでいる形である
（直近では #1330 が 5 巡かけて潰した。境界を決める規則が 2 か所にあると必ず割れる）。

🔴 **名前順は契約である。** 呼び出し元は集合へ落とすので順序を使わないが、**面の側で決めておかないと、
後から順序に依存する消費者が現れたときに壊れる**。両面の同値試験が順序込みで固定する。

## 結果

- gRPC 面を持つ east-west は **7 → 8 経路**。REST のみの残りは **6 → 5 経路**。
- 危険が 1 つ減った（意味論の逆な 2 経路による名前つきクライアントの共有が解けた）。
- `AbacEvaluator` ・下流の意味論・deploy・realm・secret・csproj は **1 行も変わらない**。
- 面が 1 本増えた（DocumentService 宛は 3 本）。**宣言の正しさと引き換えの意図的なコスト**である。
- 🔴 **#1255 は閉じない。** 残り 5 経路（検索サービスの属性値照会・文書 → 通知の送出・
  MCP のツール申告の収集・実効構成の収集ほか）が続く。

### 実測した変異（すべてビルドし直して実走。着地は `grep` で確認した）

| # | 変異 | 赤になった試験 |
| --- | --- | --- |
| M-1 | `RpcException` の縮退を `null` → 空集合 | **5**（`輸送の失敗は引けなかったへ倒す` の 5 例） |
| M-2 | 正常応答の空リストを空集合 → `null` | **1**（`空の応答は空集合であって引けなかったではない`） |
| M-3 | 書き込み側の登録を `TryAdd` → `Add` | **1**（`同じ宛先へチャネルを2本張らない`） |
| M-4 | 読み取り側の登録を `TryAdd` → `Add` | **1**（同上） |
| M-5 | 面から `[Authorize(ServiceCaller)]` を外す | **3**（未認証 / 管理者トークン / 反射の門） |
| M-6 | `ReadNamesAsync` から `OrderBy` を外す | **1**（`ListNames_is_ordered_by_name`） |
| M-7 | `MapGrpcService<TagDictionaryGrpcService>()` を外す | **6**（面が丸ごと死ぬ） |

🔴 **M-1 と M-2 が別々の試験しか殺さないことが、決定 3 の「両方向を対で」の根拠**である。
🔴 **M-3 と M-4 が同じ試験を殺すのは、その試験が登録順の 2 通りを持っているからである** ——
1 通りしか置かなければ、どちらか一方の変異は生存する。

変異はすべて戻し、**戻したことは「試験が緑に戻った」で確かめた**（`git diff` では確かめない。#1324）。
