---
title: 作業仕様書 — Wiki 前段に ABAC 適用済みの検索経路を置き、未認証時の応答を存在秘匿で固定する（#1126）
type: spec
status: done
related_ids:
  - FR-05
  - FR-13
  - FR-19
  - UC-07
  - SC-04
  - NFR
  - ADR-0004
  - ADR-0011
  - ADR-0032
  - ADR-0036
  - ADR-0046
  - ADR-0065
  - ADR-0068
  - IADR-0009
  - IADR-0020
  - IADR-0021
  - IADR-0044
  - IADR-0253
  - IADR-0256
  - IADR-0331
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/03_usecases/01_usecases.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
---

# 作業仕様書: Wiki の「検索する」と未認証応答の固定（#1126）

## 起点となる計画書（トレーサビリティ）

- ユースケース: **UC-07「Wikiで閲覧する」**
  - 事前条件: **認証済み**。対象文書の閲覧権限がある。
  - 基本フロー 1: 利用者が Wiki で文書を**開く／検索する**。
  - 基本フロー 2: **ABAC で閲覧可否を判定し、権限内の文書を表示する。**
  - 例外フロー: **権限外の文書は一覧・本文のいずれにも表示しない。**
- 機能要求: **FR-13**（正規化文書を Wiki サービスで閲覧できる。ABAC・横断検索と統合）／
  **FR-05**（ABAC。deny-by-default）／**FR-19**（個人資料は所有者ベースの判定）
- 画面: **SC-04**（Wiki.js 別ホスト。SPA 側は導線のみ）
- 計画 ADR: **ADR-0011**（Wiki.js 採用。**Wiki.js 側のページ／グループ権限は属性ベース細粒度判定の
  代替としない**。ABAC が単一真実源）／ADR-0004（ABAC）／ADR-0032（SPA 認証は BFF セッション）／
  ADR-0036・ADR-0046（所有者ベース・個人資料は Wiki へ同期しない）／ADR-0065・ADR-0068（Vertical Slice）
- 実装 ADR: [[IADR-0009]]（権限外・不存在をともに 404 にする存在秘匿）／[[IADR-0020]]（WikiService は
  Wiki.js 前段の認可ゲートウェイ。本文を自前で持たない）／[[IADR-0021]]（GraphQL API）／
  [[IADR-0044]]（多層防御）／[[IADR-0253]]（分岐は選言）／[[IADR-0256]]（縮退と故障の切り分け）
- 本作業の実装 ADR: **[[IADR-0331]]**（`develop` の最大値 +1。並行 PR と衝突したらマージ直前に改番する）

## 母集合（自分で引いた。結果と除外理由）

**母集合の定義: 「UC-07 の逐語（事前条件・基本フロー・例外フロー・事後条件）のうち、実装またはテストが
無いもの」。** issue #1126 本文の数えは転記せず、下の走査で引き直した（規則 9）。

走査は `src/knowledge/backend/Services/WikiService` と `docs/tests/UC-07_wiki-browsing.md` を対象にした。
**否定の結論には陽性対照を対で置いた**（0 件が「無い」なのか「grep が空振り」なのかを区別するため）。

| 軸 | 逐語 | 走査 | 結果 | 判定 |
| --- | --- | --- | --- | --- |
| 1 | 基本 1「開く」 | `grep -rn MapGet` | 3 件（`/pages` `/pages/{slug}` `/pages/by-doc/{id}`） | 実装あり・テストあり |
| 2 | 基本 1「**検索する**」 | `grep -rni search`（実装側） | **0 件**（陽性対照: 同じ範囲の `MapGet` は 3 件） | **穴（本作業で埋める）** |
| 3 | 事前条件「**認証済み**」 | `grep -rn "Unauthorized\|401\|IsAuthenticated\|RequireAuthorization"` | **0 件**（陽性対照: 同じ範囲の `anonymous` は 1 件＝`WikiAccessResolver`） | **穴（本作業で埋める）** |
| 4 | 基本 2「ABAC で判定」 | `AbacPageFilter` ＋ `AbacPageFilterTests` | 実装あり・テストあり | 充足 |
| 5 | 例外「権限外は現れない」 | `WikiEndpointsAbacTests` | 一覧・slug・by-doc の否定形と陽性対照あり | 充足 |
| 6 | 事後条件（同期側） | `DocumentSyncConsumerTests` ほか | 充足 | 充足 |

**除外したもの（本作業でやらない）と理由:**

- **`/bff/wiki/*` の新設。** 計画 SC-04 は「Wiki.js 別ホスト・基盤 SPA とは別配信」であり、SPA は
  **導線しか持たない**（`WikiAccessPage` は外部リンクを 1 本出すだけ）。既存の 3 経路にも BFF 口は無く、
  **検索だけ BFF へ出すと 4 経路のうち 1 本だけ露出面が違う**状態を作る。**画面が消費しない口を先に
  作らない**（計画外の機能追加の禁止）。露出は SC-04 の実現方式（SPA 側で描くか Wiki.js 側で描くか）が
  決まってから 1 回で行う ——「未決の設計判断」であることは `WikiAccessPage` のコメントが既に記録している。
  よって `docs/api/openapi.yaml` へは **WikiService の直接 API として**追記する（既存の `/wiki/pages`
  と同じ扱い。orval の入力は `/bff/` 配下のみなので生成物は動かない）。
- **Wiki.js 本体の検索 UI（`wiki.localhost` 直リンク）の是正。** Wiki.js 自身の検索は前段を通らない。
  これは ADR-0011 の分界（Wiki 側は表示制御に留める）と SC-04 の未決事項に跨がる**別の穴**であり、
  本 issue の受け入れ基準には無い。**「無い」で済ませず**、積み残しとして PR に明記する。
- **既存テストファイルの編集。** #1063 が全サービスの `Tests/` を `Tests/Features/<集約>/<操作>/` と
  `Tests/Domain/` へ移送中である。**新規テストは移送後の経路に置き、既存ファイルには触らない。**

## 決定 1: 受け入れ基準の (a) を採る —— 前段に ABAC 適用済みの検索経路を置く

issue #1126 は (a)「前段に ABAC 適用済みの検索経路を置く」か (b)「別 UC が担うと計画へ確認する」の
どちらかを求めている。**(a) を採る。** 理由:

- UC-07 の基本フロー 1 は「開く**／**検索する」であり、**同じ UC の中に並記されている**。別 UC（UC-01
  横断検索）へ寄せるには計画側の裁定が要り、しかも UC-01 は「1 つの窓口から横断検索」＝ RetrievalService
  の経路であって Wiki 内の絞り込みではない。**書かれているものを実装側の判断で消さない。**
- FR-13 は「ABAC・**横断検索**・AI 回答と統合」と書くが、それは検索を Wiki へ**持ち込まない**根拠には
  ならない —— 統合の相手であって代替ではない。

## 決定 2: 全文検索は Wiki.js へ委譲し、結果を前段の ABAC で再フィルタする

`GET /wiki/search?q=<query>&limit=<n>`（既定 20・上限 50）。

1. ABAC スコープを解決する。`Granted=false` なら **200 ＋ 空配列**（deny-by-default）。
2. `q` が空白のみなら **200 ＋ 空配列**（後段を叩かない）。
3. Wiki.js の `pages.search(query, locale)` を呼び、**ヒットしたパスの集合**を得る。
4. パス `doc/<guid>` から `DocumentId` を復元し、**自前の台帳（`WikiPage`）にある `Active` な行だけ**を引く。
   **台帳に無いパスは落ちる**（Wiki.js 上で人手で作られたページは ABAC 判定の足場を持たないので不可視）。
5. `AbacPageFilter.Filter` を適用し、**Wiki.js の並び順を保ったまま**返す。

**なぜ委譲するか**: 本文は前段が持たない（[[IADR-0020]]。ゲートウェイは本文を自前で保持しない）。
台帳が持つのは表題・スラッグ・タグ・属性だけなので、**前段だけで検索すると本文に当たらない**。
**なぜ再フィルタするか**: ADR-0011 が「Wiki.js 側のページ／グループ権限を属性ベース細粒度判定の代替と
しない」と定めているため、Wiki.js が返したヒットは**そのままでは 1 件も見せられない**。
本文取得（`GetRenderedContentAsync`）が既に採っている「Wiki.js へ委譲し、到達可否は前段が決める」形の
検索版であり、新しい方式ではない。

**障害時**: Wiki.js へ到達できない／GraphQL がエラーを返した場合は **502** を返す。
**200 ＋ 空で隠さない** —— 存在秘匿が区別させないのは「権限が無い」と「該当が無い」であって、
**「壊れている」は別の軸**である（[[IADR-0256]] と同じ切り分け）。502 は文書について何も語らない。

## 決定 3: 未認証時の応答は「存在秘匿（空／404）」に固定し、**コードで**固定する

issue は「401 か、存在秘匿として空／404 か。**どちらでもよいが、決めてテストで固定する**」と書く。
**存在秘匿を採る。**

- **エッジは BFF である**（ADR-0032 / Token Handler）。WikiService は mesh 内の後段であり、
  ここを 401 にしても利用者が見る挙動は変わらない。一方、既存 3 経路は**空／404 を返す**契約で
  テストが書かれており、**401 へ変えると 4 経路のうち 3 本の契約が黙って変わる**。
- **ただし現状は「固定されていない」。** `WikiAccessResolver` は未認証でも `anonymous` を認可サービスへ
  投げるだけで、**利用者条件を持たないポリシーが 1 件でも入れば匿名にも許可が下りる**
  （[[IADR-0044]] の多層防御が 1 枚しかない状態。`SearchBffEndpoints` が #656 で同じ理由を挙げている）。
- よって **`WikiAccessResolver` に「未認証なら認可サービスを呼ばずに `Granted=false` を返す」短絡を置く**。
  これで応答は**ポリシーの内容に依らず**空／404 に定まる。**テストは「認可サービスが全許可を返す構えでも
  匿名は空／404 になり、かつ認可サービスが呼ばれていないこと」**を測る（陽性対照: 認証済みなら呼ばれて
  200 が返る）。**fail-closed に見えるだけの状態と、契約として固定された状態を区別する。**

## 変更するファイル

- 追加 `src/knowledge/backend/Services/WikiService/Features/Wiki/SearchPages/Endpoint.cs`
- 変更 `src/knowledge/backend/Services/WikiService/Features/Wiki/WikiEndpoints.cs`（登録 1 行）
- 変更 `src/knowledge/backend/Services/WikiService/Domain/Ports/IWikiJsClient.cs`（`SearchAsync` 追加）
- 変更 `src/knowledge/backend/Services/WikiService/Infrastructure/ExternalServices/WikiJsGraphQlClient.cs`
- 変更 `src/knowledge/backend/Services/WikiService/Infrastructure/ExternalServices/WikiAccessResolver.cs`
- 追加 `src/knowledge/backend/Services/WikiService/Tests/Features/Wiki/SearchPages/WikiSearchAbacTests.cs`
- 追加 `src/knowledge/backend/Services/WikiService/Tests/Features/Wiki/AnonymousAccessContractTests.cs`
- 追加 `.ai-context/adr/IADR-0331_wiki-search-delegated-with-gateway-abac.md` ＋ 索引
- 変更 `docs/tests/UC-07_wiki-browsing.md`（未実施 2 件を削除しテストケース表へ移す）
- 変更 `docs/api/openapi.yaml`（`/wiki/search`）
- 変更 `scripts/test-spec-coverage-baseline.json`（`--update`）

## 受け入れ基準（Given-When-Then）

1. Given UC-07 基本フロー 1 の「検索する」 / When 満たし方を決める / Then **(a) を確定**し、前段に
   ABAC 適用済みの検索経路が在る。
2. Given (a) / When 実装する / Then **権限外の文書が検索結果に現れない**ことと**陽性対照（見えるものは
   見える）**の両方をテストが押さえている。
3. Given 未認証の要求 / When Wiki 前段の **4 経路**を叩く / Then 応答が固定されている（一覧・検索は
   200 ＋ 空、個別は 404）。**認可サービスが全許可を返す構えでも変わらない。**
4. Given `docs/tests/UC-07_wiki-browsing.md` / When 「未実施」節を読む / Then 本作業で埋まった 2 項目が
   削除され、テストケース表へ移っている。
5. Given `node scripts/check-test-spec-coverage.js` / When 実行する / Then 成功する。
6. Given 稼働 k3s / When wiki-service のイメージだけ差し替えて叩く / Then 認証済み検索（陽性）と
   未認証（陰性・固定した応答）が生出力で示せる。

## 実測（2026-09-02・稼働 k3s。wiki-service のイメージだけ `:issue-1126` へ差し替え）

**同じクエリ `q=verification` で 3 段の対比を取った。** 委譲先が当たりを持っていることを先に示してから
前段の応答を見ないと、「空」が「権限で落ちた」なのか「そもそも当たりが無い」なのかを区別できない。

```console
--- (1) 委譲先 Wiki.js 本体は 1 件返す（前段を通さない直叩き・API キー） ---
$ curl -s -X POST -H "Authorization: Bearer <wikijs-sync apiKey>" -H "Content-Type: application/json" \
    --data @gql.json http://127.0.0.1:18097/graphql
{"data":{"pages":{"search":{"results":[{"title":"FR-08 565 verification doc",
  "path":"doc/6cdfee53-982e-449c-9541-ffd9a707db27","locale":"ja"}],"totalHits":1}}}}
http=200

--- (2) 前段 /wiki/search 認証済み（陽性） ---
$ curl -s -H "Authorization: Bearer <abac-seeder client_credentials>" \
    "http://127.0.0.1:18099/wiki/search?q=verification"
[{"id":"bafe665f-b6d7-4004-a46c-0b3da3101287","documentId":"6cdfee53-982e-449c-9541-ffd9a707db27",
  "title":"FR-08 565 verification doc","slug":"fr-08-565-verification-doc",
  "wikiPath":"doc/6cdfee53-982e-449c-9541-ffd9a707db27","syncedAt":"2026-09-02T10:21:44.894576+00:00"}]
http=200

--- (3) 前段 /wiki/search 未認証（陰性・固定した応答） ---
$ curl -s "http://127.0.0.1:18099/wiki/search?q=verification"
[]
http=200
```

未認証の 4 経路（決定 4 の契約）:

```console
$ curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:18099/wiki/pages                  # 本文 []
200
$ curl -s -o /dev/null -w "%{http_code}\n" "http://127.0.0.1:18099/wiki/search?q=規程"        # 本文 []
200
$ curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:18099/wiki/pages/by-doc/1111...
404
$ curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:18099/wiki/pages/some-slug
404
```

サービスのログが、認証済みの往復でだけ認可サービスと Wiki.js が呼ばれていることを示す:

```console
Start processing HTTP request POST http://authorization-service:8080/authz/scope   → 200
Start processing HTTP request POST http://wiki-js:3000/graphql   (IWikiJsSearchClient)   → 200
Executed DbCommand ... WHERE p."Status" = 'active' AND p."DocumentId" = ANY (@order)
```

**測り終えたあとイメージは `:latest` へ戻した**（同じクラスタで別の実測が並行しているため、未マージの
コードを常駐させない）。再現するには `nerdctl --namespace k8s.io build -f
src/knowledge/backend/Services/WikiService/Dockerfile -t k3d-local/microservices-platform/wiki-service:issue-1126 .`
のあと `kubectl -n microservices-platform set image deploy/wiki-service wiki-service=...:issue-1126`。

### 単体テストの変異確認（自分の陽性対照）

未認証の短絡を `if (false && …)` へ潰して `AnonymousAccessContractTests` を走らせた:

```console
失敗!   -失敗:     4、合格:     1、スキップ:     0、合計:     5
```

**匿名の 4 本が落ち、陽性対照（認証済み）だけが残る。** テストが短絡そのものを見ていることの確認である。
