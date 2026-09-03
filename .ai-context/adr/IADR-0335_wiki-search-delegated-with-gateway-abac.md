---
title: IADR-0335 Wiki の「検索する」は Wiki.js へ委譲して前段の ABAC で絞り直し、未認証は認可サービスを呼ばずに存在秘匿へ倒す
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - FR-13
  - FR-19
  - UC-07
  - SC-04
  - ADR-0004
  - ADR-0011
  - ADR-0032
  - ADR-0036
  - ADR-0065
  - ADR-0068
  - IADR-0009
  - IADR-0020
  - IADR-0021
  - IADR-0044
  - IADR-0253
  - IADR-0256
author: claude
created: 2026-09-02
updated: 2026-09-03
---

# IADR-0335: Wiki の検索は委譲＋前段 ABAC、未認証は存在秘匿で固定する（#1126）

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: claude（実装）

## コンテキストと課題

UC-07 の基本フロー 1 は「利用者が Wiki で文書を**開く／検索する**」と書く。**「開く」は 3 経路が実装
されていたが、「検索する」は実装もテストも無かった。** また事前条件「**認証済み**」に対応する応答
（未認証時に何が返るか）を固定するものが、コードにもテストにも無かった。

実測（`develop` `89b4d26e`。**否定形には陽性対照を対で置いた**）:

```console
$ grep -rni "search" --include=*.cs src/knowledge/backend/Services/WikiService | grep -v Tests
（0 件）
$ grep -rn "MapGet" --include=*.cs src/knowledge/backend/Services/WikiService | grep -v Tests
Features/Wiki/GetPageByDocument/Endpoint.cs:13:        g.MapGet("/pages/by-doc/{documentId:guid}", ...
Features/Wiki/GetPageBySlug/Endpoint.cs:13:        g.MapGet("/pages/{slug}", ...
Features/Wiki/ListPages/Endpoint.cs:14:        g.MapGet("/pages", ...
```

```console
$ grep -rn "Unauthorized\|401\|IsAuthenticated\|RequireAuthorization" --include=*.cs src/knowledge/backend/Services/WikiService
（0 件）
$ grep -rn "anonymous" --include=*.cs src/knowledge/backend/Services/WikiService
Infrastructure/ExternalServices/WikiAccessResolver.cs:15:        var userId = ctx.User.Identity?.Name ?? "anonymous";
```

課題は 2 つに分かれる。

1. **「検索する」をどう満たすか。** Wiki.js 本体には検索 UI がある（SC-04 は Wiki.js の公開 URL へ
   外向きリンクを出すだけである）。しかし **Wiki.js 本体の検索結果は前段の ABAC を通っていない**。
   ADR-0011 は「Wiki.js 側のページ／グループ権限を属性ベース細粒度判定の代替としない」と定めており、
   **それで満たしたことにはできない。**
2. **未認証時の応答が固定されていない。** 前段は認可属性の解決結果で fail-closed になるので実害は
   出ていなかった。しかし `WikiAccessResolver` は未認証でも `anonymous` を認可サービスへ投げるだけで
   あり、**利用者条件を持たないポリシーが 1 件でも入れば匿名にも許可が下りる**。
   **fail-closed に「見えていた」のであって、契約として固定されてはいなかった。**

## 決定

### 決定 1: 前段に ABAC 適用済みの検索経路を置く（issue #1126 の選択肢 (a)）

`GET /wiki/search?q=<query>&limit=<n>`（既定 20・上限 50）を Vertical Slice
`Features/Wiki/SearchPages/` に置く（ADR-0065 / ADR-0068）。

(b)「検索は横断検索（別 UC）が担うと計画へ確認する」は採らない。UC-07 は「開く**／**検索する」と
**同じ UC の中に並記**しており、UC-01 の横断検索は「1 つの窓口から横断検索」＝ RetrievalService の
経路であって Wiki 内の絞り込みではない。**書かれているものを実装側の判断で消さない。**

### 決定 2: 全文検索は Wiki.js へ委譲し、結果を台帳と突き合わせて前段の ABAC で絞り直す

1. ABAC スコープを解決する。`Granted=false` なら **200 ＋ 空**（deny-by-default。後段を叩かない）。
2. `q` が空白のみなら **200 ＋ 空**（後段を叩かない）。
3. Wiki.js の `pages.search(query, locale)` を呼ぶ。
4. ヒットのパス `doc/<guid>` から `DocumentId` を復元し、**台帳（`WikiPage`）にある `Active` な行だけ**
   を引く。**台帳に足場を持たないページ（Wiki.js 上で人手で作られたもの）は落ちる。**
5. `AbacPageFilter` を適用し、**Wiki.js の関連度順を保ったまま**返す。本文は含めない。

**なぜ委譲するか**: 本文は前段が持たない（[[IADR-0020]]）。台帳が持つのは表題・スラッグ・タグ・属性
だけなので、**前段だけで検索すると本文に当たらない。**
**なぜ絞り直すか**: ADR-0011 の分界により、Wiki.js が返したヒットは**そのままでは 1 件も見せられない。**
本文取得（`ProxyOrNotFoundAsync`）が既に採っている「Wiki.js へ委譲し、到達可否は前段が決める」形の
検索版であり、新しい方式ではない。

**障害時は 502 を返す。200 ＋ 空で隠さない。** 存在秘匿（[[IADR-0009]]）が区別させないのは
「権限が無い」と「該当が無い」であって、**「後段が壊れている」は別の軸**である（[[IADR-0256]]）。
502 は文書について何も語らないので、秘匿は崩れない。

### 決定 3: 検索の委譲口は同期の口と分ける（`IWikiJsSearchClient`）

`IWikiJsClient`（upsert / 本文取得 / アーカイブ / 削除）に `SearchAsync` を足すと、**既存の 5 つの
スタブ実装がすべて実装を強いられる**（実測: `TestWebApplicationFactory` / `DocumentSyncConsumerTests` /
`DocumentDeleteArchiveSyncTests` / `PipelineRecomposeTests` / `Knowledge.IntegrationTests`。
`dotnet build` が CS0535 を 4 件出した）。**既存テストは #1063 が `Tests/Features/…` へ移送中**であり、
同じファイルを両側から触ると衝突する。

インターフェイス分離としても筋が通る —— 検索は読み取り経路の関心であり、同期・削除の面を一緒に
背負わせる理由が無い。実装クラスは `WikiJsGraphQlClient` で共通、`HttpClient` の設定
（接続先・API キー）は **`Program.cs` の 1 箇所**から両方へ与える（解決点を 2 つに増やさない）。

### 決定 4: 未認証時の応答は「存在秘匿（一覧・検索は 200 ＋ 空、個別は 404）」に固定し、コードで固定する

`WikiAccessResolver` に **「未認証なら認可サービスを呼ばずに `Granted=false` を返す」短絡**を置く。
これで匿名の応答が**ポリシーの内容に依らず**定まる。

**401 にはしない。** エッジは BFF（ADR-0032 / Token Handler）であり、WikiService は mesh 内の後段である。
ここを 401 にしても利用者が見る挙動は変わらない一方、既存 3 経路は空／404 を返す契約でテストが
書かれており、**401 へ変えると 4 経路のうち 3 本の契約が黙って変わる。**

**短絡は `WikiAccessResolver`（Infrastructure）に置き、エンドポイントには置かない。** 認可解決を
スタブへ差し替えている既存テスト（`WikiEndpointsAbacTests` ほか）の意味を変えないためである。

## 理由

- ADR-0011 の分界（ABAC は本システムが単一真実源・Wiki.js は表示制御）を、検索という新しい経路にも
  **同じ形で**適用できる。前段が持っていない能力（本文の全文検索）だけを借り、判断は借りない。
- 「fail-closed に見える」と「契約として固定されている」を分けた。テストは**認可サービスを全許可
  （`granted: true`・条件なし）で応答する構え**に置いたうえで、匿名が空／404 になること・
  **認可サービスが 1 回も呼ばれないこと**・陽性対照として認証済みなら呼ばれて 200 になることを測る。
  ②が無いと「たまたま拒否された」と区別できず、③が無いと「常に拒否する実装」が①を通す。

## 結果

- 良い影響: UC-07 基本フロー 1 の後半が実装とテストの両方で埋まる。匿名の応答が
  ポリシーに依らず定まり、[[IADR-0044]] の多層防御が 1 枚から 2 枚になる。
- 悪い影響 / トレードオフ:
  - **Wiki.js の検索エンジン設定に結合する。** Wiki.js 側の検索エンジンが未設定なら
    `pages.search` はエラーを返し、本経路は 502 になる（**空ではなく故障として見える** ——
    これは意図した挙動である）。
  - **Wiki.js 本体の検索 UI（`wiki.localhost` へ直接遷移した先）は前段を通らないままである。**
    これは ADR-0011 の分界と SC-04 の未決事項（ページツリー・本文を SPA 側で描くか Wiki.js 側で描くか）に
    跨がる**別の穴**であり、本決定では閉じない。**「無い」で済ませず、残っていることを記録する。**
  - **`/bff/wiki/*` は作らない。** SC-04 は「Wiki.js 別ホスト・基盤 SPA とは別配信」であり SPA は導線
    しか持たない。既存 3 経路にも BFF 口が無く、**検索だけ露出面が違う**状態を作らない。
    露出は SC-04 の実現方式が決まってから 1 回で行う。
- フォローアップ: 上の 2 点（Wiki.js 本体検索の扱い・BFF 露出）は SC-04 の実現方式が決まる時点で
  まとめて扱う。

［2026-09-03 追記 / #1199］**フォローアップの 2 点に決着が付いた。決定 1〜4 は変えていない。**

- **「`/bff/wiki/*` は作らない」は解けた。** 計画 ADR-0073（Accepted / 2026-09-03）決定 2 が
  SC-04 の実現方式を「基盤 SPA のルートとし、BFF 経由で取得して SPA が描く」と確定させ、
  決定 4 が「**IADR-0335 が BFF 口を作らなかった判断は正しかった。本決定がその『1 回でまとめて
  行う』時点である**」と明記した。よって 4 経路をまとめて開いた。**中継の形は [[IADR-0361]] が正本**
  である（透過中継・Authorization の伝播・BFF 側に ABAC を置かない・未認証は BFF で 401）。
- **「Wiki.js 本体の検索 UI が前段を通らない」も同 ADR 決定 1・3 が閉じた** ——
  塞ぐべきは UI の一機能ではなく到達経路であり、stg/prod では `wikijs.ingress.enabled: false` により
  既に塞がっている。**local の露出は決定 5 のとおり開発ランタイムの範囲として残る。**
- 🔴 **上の「401 にはしない」は依然として正しい。** あれは *WikiService*（mesh 内の後段）の話であり、
  同じ文が「エッジは BFF」と 401 の置き場所を名指ししている。BFF 口は 401 を返すが、
  **未認証は後段へ到達しない**ので、ここで固定した契約（一覧・検索は 200 ＋ 空、個別は 404）は動かない。

## 関連

- 起点: UC-07（基本フロー 1・事前条件）／FR-13／FR-05／SC-04／ADR-0011／ADR-0032
- 実装記録: [[IADR-0009]]（存在秘匿）／[[IADR-0020]]（前段ゲートウェイ）／[[IADR-0021]]（GraphQL）／
  [[IADR-0044]]（多層防御）／[[IADR-0253]]（分岐は選言）／[[IADR-0256]]（縮退と故障の切り分け）
- issue: #1126（本決定）／#1106（穴の出所）／#1063（`Tests/` 移送。決定 3 の制約）
