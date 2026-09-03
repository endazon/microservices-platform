---
title: /bff/wiki/* の 4 経路を開き、Wiki 前段を画面用の口へ露出する
type: spec
status: in-progress
related_ids:
  - FR-13
  - UC-07
  - SC-04
  - ADR-0011
  - ADR-0032
  - ADR-0073
  - IADR-0009
  - IADR-0020
  - IADR-0044
  - IADR-0089
  - IADR-0285
  - IADR-0300
  - IADR-0335
  - IADR-0346
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0073_wikijs-ui-not-exposed-sc04-via-gateway.md 決定 2・4
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md（分界。2026-08-15 追記）
  - planning:projects/microservices-platform/03_usecases/01_usecases.md UC-07
  - planning:projects/microservices-platform/05_screens/01_screens.md SC-04
---

# 仕様書: `/bff/wiki/*` の 4 経路（issue #1199）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-13（Wiki 閲覧・ABAC 適用）
- ユースケース（UC）: UC-07（Wiki で閲覧する。基本フロー 1「開く／検索する」）
- 画面（SC）: SC-04（Wiki 閲覧。**画面実装そのものは本作業の対象外**＝ #1200）
- 関連 ADR: ADR-0073（決定 2・4）／ADR-0011（分界）／ADR-0032（エッジは BFF）／ADR-0011

## 目的・背景

ADR-0073 決定 1 は「利用者が Wiki の内容へ到達する経路は WikiService（`/wiki/*`）の 1 本とする」と
定め、決定 2 は「SC-04 は基盤 SPA のルートとし、BFF 経由で取得して SPA が描く」と確定させた。
決定 4 は **`/bff/wiki/*` を 4 経路まとめて開く**ことを指示し、あわせて
「**`IADR-0335` が BFF 口を作らなかった判断は正しかった。本決定がその『1 回でまとめて行う』時点である**」
と明記している。

本作業はその**前半（BFF 口）だけ**を担う。SC-04 の画面（ページツリー・本文・検索欄）は #1200 に切る
—— 画面は BFF 口が無いと描けないので、こちらを先に通す。

## 母集合（自分で引いた。issue の数えは転記していない）

**引いた日時**: 2026-09-03。**基点**: `develop` `45853885`。
`git rev-parse --is-shallow-repository` = **`false`**（履歴の打ち切りではないので出典に使える）。

### 軸 1: 後段に在って BFF に無い Wiki 経路

```console
$ grep -rn "MapGet\|MapPost\|MapGroup" --include=*.cs src/knowledge/backend/Services/WikiService | grep -v "/Tests/"
.../Features/Wiki/GetPageByDocument/Endpoint.cs:13:  g.MapGet("/pages/by-doc/{documentId:guid}", …
.../Features/Wiki/GetPageBySlug/Endpoint.cs:13:     g.MapGet("/pages/{slug}", …
.../Features/Wiki/ListPages/Endpoint.cs:14:         g.MapGet("/pages", …
.../Features/Wiki/SearchPages/Endpoint.cs:29:       g.MapGet("/search", …
.../Features/Wiki/WikiEndpoints.cs:28:              var g = app.MapGroup("/wiki").WithTags("Wiki");
```

**後段は 4 経路。書き込みの口は 1 本も無い**（`MapPost` / `MapPut` / `MapDelete` が 0 件＝上の走査に
現れない）。したがって開ける口は 4 本が上限であり、**4 本すべてを開く**（ADR-0073 決定 4 の逐語）。

```console
$ grep -c "wiki\|Wiki" src/platform/backend/Bff/Platform.Bff/Composition/BffEndpointComposition.cs
0
```

**陽性対照**: 同ファイルの `new DelegateBffEndpointModule` は **19 件**ある
（`grep -c` = 19）。**grep が空振りしたのではなく、Wiki だけが無い。**

```console
$ git grep -n "bff/wiki" -- .
.ai-context/adr/IADR-0335_…:143:  「`/bff/wiki/*` は作らない。」
.ai-context/specs/20260902_issue-1126_…:76:「`/bff/wiki/*` の新設。」
```

ヒットする 2 件はいずれも**「作らない」と書いた凍結記録**であり、実装は 0 件である。

### 軸 2: 宣言だけあって実体の無い設定

```console
$ git grep -n "Services__WikiService\|Services:WikiService" -- .
.ai-context/adr/IADR-0089_…:45    （記録。「named client 不在の宙ぶらりん項目」と当時から書かれている）
.ai-context/specs/20260720_issue-342_…:57（同上）
.ai-context/specs/20260720_issue-344_…:45（同上）
.ai-context/superpowers/plans/2026-06-26-P0-foundation.md:2111（凍結計画）
deploy/helm/microservices-platform/values.yaml:688: - name: Services__WikiService
$ grep -c 'AddHttpClient("' src/platform/backend/Bff/Platform.Bff/Program.cs
14
```

- **helm には宛先が在る**（`http://wiki-service:8080`）。
- 🔴 **`deploy/docker-compose.yml` の bff env には `Services__WikiService` が無い**
  （上の走査の追跡下ファイルは helm 1 件だけ＝**陽性対照は同じ走査が helm を返していること**）。
  compose のサービス名は `wiki-service`、公開ポートは `:8080`（`deploy/docker-compose.yml:448`）なので、
  **コード既定を `http://wiki-service:8080` にすれば compose の上書きは不要**である
  （`NotificationService` と同じ形。IADR-0346 決定 6）。
- `Platform.Bff/Program.cs` の named client は 14 件で **Wiki は無い**。

### 軸 3: 契約（openapi）の欠落

```console
$ grep -n "^  /wiki\|^  /bff/wiki" docs/api/openapi.yaml
1943:  /wiki/pages:
1952:  /wiki/search:
1980:  /wiki/pages/{slug}:
$ grep -c "by-doc" docs/api/openapi.yaml
0
$ grep -c "^  /bff/" docs/api/openapi.yaml
66
```

**陽性対照**: `/bff/` で始まる経路は 66 本ある。**`/bff/wiki/*` だけが無い**。あわせて
**サービス直の `/wiki/pages/by-doc/{documentId}` も欠けている**（4 経路のうち 1 本だけ契約に無い）。

### 軸 4: テストの置き場所（鏡写しか平置きか）

```console
$ find src/platform/backend/Bff/Platform.Bff.Tests -type d
src/platform/backend/Bff/Platform.Bff.Tests
```

**下位ディレクトリが 0 件＝全 45 ファイルが平置き**である。`#1063`（`Tests/Features/…` への鏡写し移送）
は `Platform.Bff.Tests` を射程に含んでいない（IADR-0346 §結果が同じことを実測つきで記録している）。
**したがって本作業も平置きに従う**（既存 44 ファイルと違う置き方を 1 本だけ持ち込まない）。

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| `.ai-context/adr/IADR-0335`・`.ai-context/specs/20260902_issue-1126_*` の本文 | **確定済みの凍結記録**であり、本文を書き換えない（`traceability.repo.md`）。IADR-0335 へは**日付つき追記**だけを足す（同規約が `.ai-context/specs/` と `.ai-context/adr/` の追記を認める形式） |
| `.ai-context/superpowers/plans/*` | 凍結記録。**追記も不可** |
| `src/knowledge/backend/Services/WikiService/**` | ADR-0073 §結果「**`WikiService` の実装（4 経路・ABAC・存在秘匿・502 の切り分け）は変更不要である**」。触らない |
| `deploy/local/**`（`WIKI_BASE_URL` / `admin-ingress-wiki.yaml`） | ADR-0073 決定 5 が **dev の直接露出を維持**すると定めている。本 issue の射程外（統制が及ばない範囲として ADR が既に記録済み） |
| `src/knowledge/frontend/src/features/sc04-wiki/**` | SC-04 の画面実装は #1200。**本 issue は BFF 口だけ**（issue §やること・交差の宣言） |
| `deploy/docker-compose.yml` | 軸 2 の実測により**上書きが不要**（コード既定が `:8080`）。issue §宣言ファイル領域も「欠けていた場合のみ」と条件付きで挙げている |

## 対象範囲

- **対象**
  - `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/WikiBffEndpoints.cs`（新規・4 経路）
  - `src/platform/backend/Bff/Platform.Bff/Composition/BffEndpointComposition.cs`（1 行）
  - `src/platform/backend/Bff/Platform.Bff/Program.cs`（named client `WikiService`）
  - `src/platform/backend/Bff/Platform.Bff.Tests/`（新規テスト＋合成点テストの件数・群集合）
  - `docs/api/openapi.yaml`（`/bff/wiki/*` 4 本 ＋ サービス直の `by-doc` 1 本 ＋ 3 スキーマ）
  - orval 生成物（`src/platform/frontend/src/lib/api/generated/`）
  - `docs/api/BFF_bff-surface.md`（エンドポイント一覧へ 4 行）
  - `docs/tests/UC-07_wiki-browsing.md`（§未実施 の該当項目 → テストケース表へ）
  - `.ai-context/adr/IADR-0355`（新規）＋ `.ai-context/adr/README.md`（索引）
  - `.ai-context/adr/IADR-0335_*.md`（**日付つき追記のみ**）
- **対象外**: SC-04 の画面実装（#1200）／WikiService 本体／dev の直接露出／Wiki.js での編集（ADR-0073 決定 6 が未決）

## 設計

### 判断 1: 置き場所は `Knowledge.Bff.Endpoints`

後段 `WikiService` は **knowledge ユニット**のサービス（`src/knowledge/backend/Services/WikiService`）で
ある。`TagDictionaryBffEndpoints`（後段 DocumentService）・`PrivateNoteBffEndpoints`（同）・
`GraphBffEndpoints`（後段 GraphService）と同じ切り分けであり、platform 同居にしない
（platform 同居は後段が platform ユニットのとき＝ `McpClient` / `UserAdmin` / `Notification`。IADR-0346 決定 1）。

### 判断 2: 資格情報は **Authorization の伝播**（方式 A）

本リポジトリの BFF には権限伝播が 2 方式ある（`GraphBffEndpoints` 冒頭が正本）。

- A) 利用者の JWT を後段へ伝播する
- B) BFF が解決した `AccessScope` を本文へ載せる（`SearchBffEndpoints` → RetrievalService）

**判断の軸は「後段が自分で ABAC を解決する型かどうか」**である。WikiService は
`IWikiAccessResolver`（`WikiAccessResolver` が `/authz/scope` を叩く）で**自分で解決する型**なので **A** を採る。
B を採ると、その経路へ到達できる誰もが任意の scope を主張できる。

🔴 **伝播を落とすと「全部空・全部 404」で静かに壊れる。** `WikiAccessResolver` は未認証を
`Granted=false` へ短絡させる（IADR-0335 決定 4）ので、ヘッダが届かないと**一覧・検索は 200 ＋ 空、
個別は 404** になる —— *動くように見える壊れ方*である。よって**陽性対照つきのテストで固定する**。

### 判断 3: BFF 側に ABAC の前段（`BffScopeResolver`）を置かない

`GraphBffEndpoints` と同じ理由である。置くと得るものが無いまま次の 3 つだけが増える。

1. 拒否が **403** になり、後段が 404 へ倒している存在秘匿と応答が割れる（IADR-0009）
2. ABAC の判断点が 2 つになり、片方が腐っても気付けない
3. 要求ごとに `/authz/scope` の往復が 1 つ増える（後段が同じ往復を必ず行うため**二重になる**）

**後段の門が BFF に置ける門を包含する** —— BFF に置ける門は `Granted` だけで、
文書条件（`AbacPageFilter`）は台帳の行が要るため BFF では当てられない。

### 判断 4: 応答は透過する。状態コードを作り替えない。不達は 502

- 後段の **404**（権限外・不存在・アーカイブ済みを区別しない。IADR-0009）を**そのまま返す**。
  403 や 200 へ変換すると**存在秘匿が BFF 層で破れる**。
- 一覧・検索の **200 ＋ 空**もそのまま返す（「権限が無い」と「該当が無い」を区別させない）。
- 後段の **502**（Wiki.js 不達。IADR-0335 決定 2）もそのまま返す。
- **BFF から後段へ到達できない場合も 502**。空の 200 で隠すと「Wiki に何も無い」と読めてしまう。

### 判断 5: 未認証は **401**（後段の契約と食い違わない）

群に `RequireAuthorization()` を付ける。**NFR-09 の暫定運用「エッジ（BFF）で OIDC/JWT を担保する」**
（#656）と、`check-bff-authz-docs.js` の不変条件「`/bff/*` に無認証の端点は存在してはならない」に従う。

🔴 **IADR-0335 決定 4 と矛盾しない。** 同決定は「**401 にはしない。エッジは BFF（ADR-0032 / Token
Handler）であり、ここは mesh 内の後段である**」と書いており、**401 を置く場所として BFF を名指ししている**。
未認証の要求は BFF で 401 になり**後段へ到達しない**ので、後段の契約（一覧・検索は 200 ＋ 空、個別は 404）は
1 ミリも動かない。**2 つの層が違う応答を返すことが食い違いなのではなく、同じ層で応答が定まらないことが
食い違いである**（IADR-0335 が塞いだのは後者）。

### 判断 6: クエリは**指定されたものだけ**を後段へ載せる

`q` / `limit` を型付きで受け、**指定されたときだけ**後段のクエリへ載せる。
**既定（20）・上限（50）のクランプは後段（`SearchWikiPagesEndpoint`）が唯一の情報源**である
（IADR-0346 決定 4 と同じ理由 —— BFF に 2 つ目のクランプを置くと、後段を変えたとき BFF だけ古い値で切る）。
生のクエリ文字列を素通しにもしない（後段の面に無いパラメータを無検査で渡す口を作らない）。

### 判断 7: named client のコード既定は `:8080`。readiness には入れない

```csharp
builder.Services.AddHttpClient("WikiService", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:WikiService"]
        ?? "http://wiki-service:8080"));
```

後発サービスの規約（IADR-0089 / #342 の「上書き漏れで 21 秒タイムアウト → 502」の面を最初から作らない）。
ホスト名 `wiki-service` は compose のサービス名・helm の `{{ $name }}-service`（chart キー `wiki`）と
文字列一致する。**helm の既存上書きと同値**なので manifest 側の追加・変更は無い（軸 2）。

**readiness の `UriHealthCheck` には入れない** —— Wiki 閲覧は 1 機能であり、後段の不調で BFF 全体を
not-ready にするのは fail-safe の後退である（`McpServer` / `DocumentService` / `NotificationService` も
入っていない＝実測）。

### 判断 8: パス変数は後段の形をそのまま持つ

- `{slug}` は `string`（後段が `string`）。
- `{documentId:guid}` は **`Guid` 制約つき**にする —— 後段が `{documentId:guid}` で受けており、
  BFF で `string` にすると**形式不正の 400 の出所が 2 か所**になる（`GraphBffEndpoints` の
  `{documentId:guid}` と同じ扱い）。**ルートの衝突は起きない**: `/pages/{slug}` と
  `/pages/by-doc/{documentId:guid}` は**セグメント数が違う**（2 対 3）。

### 判断 9: 契約に応答スキーマを置く（#1200 が使う）

`/bff/wiki/*` の応答は `components.schemas` に `WikiPageSummary` / `WikiSearchHit` / `WikiPageView` を
新設して参照する（**サービス直の `/wiki/*` 3 本は従来どおり散文のまま**にし、`by-doc` の 1 本だけ
欠落を埋める）。**C# 契約 record は作らない** —— 形は WikiService の内部 record であり、
`Shared.Contracts` へ持ち上げると `check-contract-schema.js` の baseline を動かす一方、
BFF は本文を**そのまま透過**するので型は要らない（`check-openapi-dto-drift.js` は同名の C# record が
無いスキーマを対象外にする＝実測: `findDrift` の `if (!csProps) continue;`）。

## 受け入れ基準

- [ ] `/bff/wiki/pages`・`/bff/wiki/search`・`/bff/wiki/pages/{slug}`・`/bff/wiki/pages/by-doc/{documentId}`
      の 4 経路が後段 WikiService へ到達し、**後段の状態コードと本文がそのまま返る**
- [ ] 資格情報（`Authorization`）が後段へ伝播する（**陽性対照つき**。落とすとテストが赤くなる）
- [ ] 後段の **404 が 404 のまま**返る（403 へ変換しない）
- [ ] 後段の **200 ＋ 空**がそのまま返る（**陽性対照**として中身のある 200 を同じ群で押さえる）
- [ ] **未認証は 4 経路とも 401**（BFF で止まり、後段へ到達しない）
- [ ] 後段不達は **502**
- [ ] `grep "^  /bff/wiki" docs/api/openapi.yaml` が **4 本**返す。`by-doc` も現れる
- [ ] `pnpm run codegen` の**再生成差分がゼロ**
- [ ] `node scripts/check-bff-downstreams.js` が成功する
- [ ] `node scripts/check-bff-authz-docs.js` が成功する（4 経路の `x-roles: []` と実装が一致）
- [ ] `dotnet build` / `dotnet test`（platform・knowledge 両ユニット）が 0 エラー・Failed=0
- [ ] `docs/tests/UC-07_wiki-browsing.md` §未実施 から「前段の経路を画面用の口へ出していない」が消え、
      テストケース表へ移っている
- [ ] 稼働 k3s で `https://localhost/bff/wiki/search?q=…` が WikiService の応答を返す（陽性）／
      未認証 401（陰性）／`by-doc/<存在しない id>` が 404

## テスト方針

`Platform.Bff.Tests/BffWikiEndpointTests.cs`（平置き。軸 4）に置き、`BffTestFactory` へ
`WikiStubHandler` を足す（`NotificationStubHandler` と同型 —— **資格情報が届かなければ後段役が
自分で拒否する**構えにして、伝播の陽性対照が成立するようにする）。

| 測ること | 陰性 | 陽性対照 |
| --- | --- | --- |
| 認可 | 未認証は 4 経路とも 401 | 一般利用者ロール（`viewer`）でも 200 |
| 伝播 | —— | `Authorization` が後段へ届いている |
| 経路 | —— | 後段パスが `/wiki/...`（`/bff` を剥がす）で、クエリが載る |
| 透過 | 後段 404 → 404 | 後段 200 → 200（本文つき） |
| 透過 | 後段 200 ＋ 空 → 200 ＋ 空 | 同上 |
| 故障 | 後段 502 → 502 ／ 不達 → 502 | —— |
| 上流解決 | —— | `WikiService` の `BaseAddress` が `Services:WikiService` で解決される |

`BffEndpointCompositionTests` は件数（19 → 20）と期待グループ集合（`/bff/wiki` を追加）を更新する。

## 計画書との差異

- 差異: **なし**。ADR-0073 決定 4 の逐語（4 経路まとめて開く）どおりに実装する。
  `IADR-0335` の「`/bff/wiki/*` は作らない」は**同 ADR 決定 4 が明示的に解いた**フォローアップであり、
  計画との差異ではない（実装 ADR の側が覆る）。

## 未決事項

- **SC-04 の画面**（#1200）。本作業は口を開けるだけで、画面は依然として外部リンク 1 本のままである。
  ADR-0073 §残るもの「SC-04 の画面実装が入るまで、本番で Wiki 閲覧ができない」は本作業では解けない。
- **Wiki.js 本体 UI の直接露出（local）** は ADR-0073 決定 5 の範囲として残る。
- **Wiki.js での編集の是非**は ADR-0073 決定 6 が未決のままにしている。
