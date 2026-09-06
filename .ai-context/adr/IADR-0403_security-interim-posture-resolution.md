---
title: IADR-0403 第一防御は mesh mTLS・多層防御は「口の性質ごとに違う形」であり、内部サービスへ一律に JWT を課すことはしない
type: impl-adr
status: Proposed
related_ids:
  - FR-05
  - FR-12
  - NFR-09
  - NFR-16
  - SC-07
  - ADR-0004
  - ADR-0005
  - ADR-0029
  - ADR-0075
  - IADR-0017
  - IADR-0026
  - IADR-0029
  - IADR-0042
  - IADR-0044
  - IADR-0128
  - IADR-0154
  - IADR-0377
  - IADR-0379
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md 決定
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md 決定
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md 決定
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 1・2・6
related_specs:
  - ../specs/20260906_issue-458_security-posture.md
related_adrs:
  - IADR-0026 (mesh mTLS が第一防御 —— 本 IADR はその帰結を口の単位まで下ろす)
  - IADR-0044 (バックエンドの多層防御 —— 本 IADR がフォローアップ 1 を決着させる)
  - IADR-0379 (east-west gRPC の s2s トークン —— 本 IADR が多層防御の第 2 層として位置づける)
---

# IADR-0403: 第一防御は mesh mTLS・多層防御は「口の性質ごとに違う形」であり、内部サービスへ一律に JWT を課すことはしない（#458）

- 状態: Proposed
- 日付: 2026-09-06
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: `NFR-09`（全 API で OIDC/JWT 認証）／`NFR-16`（サービス間 mTLS）／`FR-05`／`FR-12`／`SC-07`
- 関連 ADR: `ADR-0004`（Keycloak OIDC/JWT・ABAC）／`ADR-0005`（Istio mTLS）／`ADR-0029`／`ADR-0075`
- Issue: #458（親トラッカー #454。#447 からコネクタ資格情報の Vault 化を委譲されている）

## コンテキストと課題

#458 は「暫定→恒久」の 3 項目（全 API の OIDC/JWT・サービス間 mTLS 全面適用・Vault 導入）の解消を求める。
棚卸しは項目 1 を **「15 サービス中 13 が `AddPlatformAuth` を持つ。残り 2 が未達」** と読んでいた。

🔴 **この読みは、測っている対象が要求と噛み合っていない。** `AddPlatformAuth` は
`FallbackPolicy` を置かない（実測: 認可の `FallbackPolicy` は全 `src/` で 0 件。陽性対照として
`AddPolicy` は 5 件・`RequireAuthorization` は 121 件当たる）。**呼んだだけでは 1 つの口も塞がらない。**
口の側で数えると、REST にロール門を 1 つも持たないのは **2 サービスではなく 6 サービス**である
（Conversion / Ingestion / Wiki / AiAnalysis / Retrieval / LlmGateway）。

さらに、この状態は放置ではなく**世代をまたいだ明示的な判断の連なり**である:

| 記録 | 状態 | 述べていること |
| --- | --- | --- |
| `IADR-0017` 決定 3 | **Superseded by `IADR-0026`** | アプリ層の s2s JWT を**見送る**。前提は「ネットワーク分離が第一防御」 |
| `IADR-0026` 決定 1・4 | Accepted | **mesh STRICT mTLS が第一防御**。ネットワーク分離は多層防御へ格下げ。全 API の OIDC/JWT は「mTLS がワークロード認証を担保する前提の下で**段階的に進める別課題**」 |
| `IADR-0044` 決定 1〜3 | Accepted | 書き込み／管理の口へロール門を積む（多層防御）。**サービス間内部呼び出しは対象外**。`ConversionService` は対象外 |
| `IADR-0379` 決定 4 | Accepted | east-west は**呼び出し側サービス自身の資格情報**（`ServiceCaller` / `platform-service`）。🔴 **利用者トークンは east-west へ載せない** |

つまり `IADR-0017` の前提（ネットワーク分離＝第一防御）は既に置き換わっているのに、
**その上に載っていた「s2s JWT を見送る」という決定だけが、前提の交代に追随して言い直されていない。**
`IADR-0044` はフォローアップに「`ConversionService` への認可（認証基盤導入＋`IADR-0042` 決定 3 の更新）」を
残したまま 2 か月が経っている。

**決めるべきは「2 サービスへ `AddPlatformAuth` を足すか」ではない。**
**`IADR-0026` 後の第一防御は何で、`IADR-0044` の多層防御は口ごとに何を要求するか、である。**

## 検討した選択肢

### 選択肢の比較（軸を名指しする）

| | A. 全内部サービスへ一律に JWT 必須（`FallbackPolicy` を置く） | B. 口の性質ごとに層を選ぶ（**採用**） | C. 現状維持（mesh mTLS だけに委ねる） |
| --- | --- | --- | --- |
| **confused deputy** | 🔴 **悪化する。** 利用者トークンが east-west へ流れると呼び出し先は「利用者が直接呼んだ」と区別できない（`IADR-0379` 決定 4 が案 4-B として却下した形そのもの） | 起きない。east-west は s2s トークン、north-south は利用者トークンと軸を分ける | 起きない（そもそもトークンが無い） |
| **BFF 迂回への耐性** | 強い | 強い（書き込み・管理の口はロール門、s2s の口は `ServiceCaller`） | 🔴 **無い。** mesh 内から素で叩ける |
| **既存フローの破壊** | 🔴 **大きい。** トークン非保持のワーカー・内部 HTTP 呼び出し元が全て 401（`IADR-0017` が案 A として却下した理由と同じ） | 無い（層を足す向きにしか動かない） | 無い |
| **ABAC の実効** | 変わらない（ロール門は ABAC を代替しない） | 変わらない | 変わらない |
| **計画 NFR-09 との距離** | 文面には最も近い | 口の単位で近づく。**残差を明示して追跡できる** | 🔴 遠いまま・追跡もされない |
| **既存の確定記録との衝突** | 🔴 `IADR-0042` 決定 3 / `IADR-0154` 決定 6 / `IADR-0044` 決定 2 を**まとめて無断で覆す** | 衝突しない（各記録の射程を保ったまま位置づけ直す） | 衝突しない |
| **費用** | 大（全呼び出し元の資格情報配線 ＋ 回帰） | 小〜中（既に配線済みの s2s を使う） | 0 |

**A を却下する決め手は費用ではなく正しさである。** 一律必須化は「認証されていれば通る」に倒れやすく、
利用者トークンを east-west へ流す誘惑を作る。それは `IADR-0379` 決定 4 が明示的に却下した設計である。

**C を却下する決め手は `IADR-0044` の存在である。** 多層防御を置くと既に決めてある。

### 「第一防御」を選ぶ軸

| | mesh STRICT mTLS（**採用**） | ネットワーク分離 | アプリ層 JWT |
| --- | --- | --- | --- |
| ワークロード同一性の保証 | 証明書で保証する | 🔴 保証しない（同一ネットワーク内なら誰でも） | トークンで保証する |
| アプリ実装への依存 | 無い（Envoy が終端） | 無い | 🔴 全呼び出し元の実装に依存 |
| 経路上の暗号化 | ある | 🔴 無い（平文） | 🔴 無い（TLS は別途） |
| 宣言的に強制できるか | できる（`PeerAuthentication` ＋ ArgoCD の自己修復） | できる（compose / NetworkPolicy） | コードにしか書けない |

## 決定

### 決定 1: 第一防御は **Istio STRICT mTLS** である。`IADR-0026` 決定 1 を追認し、動かさない

サービス間の**ワークロード同一性と経路の秘匿**は mesh が担う。アプリ層はこれを再実装しない。
`IADR-0017` 決定 3（アプリ層 s2s JWT の見送り）は、**その前提が `IADR-0026` で置き換わったことにより
「見送り」ではなくなった** —— 見送りの理由が「mTLS が来れば不要になるから」だったのに対し、
mTLS が来た後に残る要求は**別物（多層防御）**だからである。本 IADR はここを言い直す。

### 決定 2: 多層防御は一律ではなく、**口の性質ごとに 4 つの形**を取る

🔴 **`AddPlatformAuth` の有無を多層防御の指標にしない。** 指標は「その口に到達した無資格の呼び出しが
何によって拒まれるか」である。形は 4 つある:

| 形 | 何を拒むか | 実体 |
| --- | --- | --- |
| **R. ロール門** | 資格の無い**利用者** | `RequireAuthorization(AdminOnly / ConfigViewer)` |
| **S. s2s 門** | **利用者**（管理者含む）と無資格サービス | `[Authorize(Policy = ServiceCaller)]`（`IADR-0379` 決定 4） |
| **A. 内容 ABAC** | 呼び出しは通すが、**主体が見てよいものだけ**返す | fail-closed のスコープ絞り込み（`IADR-0012` 系） |
| **N. 層なし** | （アプリ層では拒まない） | mesh mTLS ＋ ネットワーク分離 ＋ BFF の門に委ねる |

### 決定 3: 🔴 15 サービス全件の判定（**一律規則ではなく 1 件ずつ理由を持つ**）

口の数は構文で数えた（`Map(Get|Post|Put|Delete|Patch)\(` の実数。語では数えない）。
`src/ai-stock-trading` は除外している。

| # | サービス | 口 | 現在の形 | アプリ層 authn/authz を要するか | 要さないなら**何が保証を担うか** |
| --- | --- | --- | --- | --- | --- |
| 1 | `Platform.Bff` | 縁 | R（BFF セッション） | **要する（充足）** | — |
| 2 | `DocumentService` | 39 | R ＋ S | **要する（充足）** | 書き込み 13 口が `AdminOnly`、gRPC 読み口が `ServiceCaller` |
| 3 | `DataSourceService` | 7 | R | **要する（充足）** | 群全体に admin/operator、12 箇所 `AdminOnly` |
| 4 | `GraphService` | 13 | R | **要する（充足）** | 7 箇所 |
| 5 | `DashboardService` | 6 | R | **要する（充足）** | 10 箇所 |
| 6 | `FeedbackService` | 3 | R | **要する（充足）** | 4 箇所 |
| 7 | `NotificationService` | 3 | R | **要する（充足）** | 3 箇所。受け口はネットワーク分離にも載る |
| 8 | `McpServer` | 6 | R | **要する（充足）** | 管理 REST に `AdminOnly` 4 箇所 |
| 9 | `AuthorizationService` | 20 | R ＋ S ＋ N | **要する（部分）** | 管理系は `AdminOnly`。🔴 REST `/authz/scope` は無認可のまま（`IADR-0044` 決定 2）だが、**同じ評価器の gRPC 面が `ServiceCaller` を持つ**（`IADR-0379` 決定 5） |
| 10 | `RetrievalService` | 3 | **A** | **要さない** | `/search` は fail-closed ABAC（`ScopeFilter` / `HybridSearchService`）。**ロール門は ABAC を代替しない**ので積んでも保証は増えない |
| 11 | `AiAnalysisService` | 3 | **A** | **要さない** | `RagOrchestrator` が取得段の ABAC を透過。回答は Retrieval の絞り込みを超えない |
| 12 | `WikiService` | 4 | **A** | **要さない** | `AbacPageFilter` / `WikiAccessResolver` が閲覧可能ページを絞る |
| 13 | `LlmGateway` | 3 | **S**（gRPC）＋ N（REST） | **要する（部分充足）** | gRPC の `/embed`・`/complete` は `ServiceCaller`。🔴 **REST 3 口は無認可のまま並走**（`IADR-0379` 決定 5「並走中の正は REST」） |
| 14 | `ConversionService` | 5 | **N** | **要さない（記録された理由あり）** | 決定 4 を見よ |
| 15 | `IngestionService` | **0** | **N** | **要さない（口が無い）** | `app.MapPlatformIntrospection()` 1 件のみ。**副作用のある操作を 1 つも持たない** |

**要約**: 8 件が R で充足、3 件（10-12）は A が保証を担うのでロール門は無意味、
2 件（9・13）は**部分充足で残差を持つ**、2 件（14・15）は N で理由が記録済みである。

### 決定 4: `ConversionService` は **acceptable（記録された理由つき）**、`IngestionService` は **correct**

🔴 **どちらも defect ではない。** ただし根拠の強さが違うので分けて述べる。

**`IngestionService` = correct。** HTTP の口が 0 である（`MapPlatformIntrospection()` 1 行のみ。
陽性対照として DocumentService の同走査は 50 行返る）。**塞ぐべき口が無いものは塞げない。**

**`ConversionService` = acceptable。** 口は 5 本あり、うち 2 本（`POST .../retry`・
`POST .../figures/{id}/correction`）は副作用を持つ。とりわけ後者は**保存され再発行されて
一般利用者が読む本文を書き換える**。それでも defect と呼ばないのは:

1. **判断が記録されている。** `IADR-0042` 決定 3（ワーカーは最小 HTTP サーフェスに留め認可を課さない）を、
   `IADR-0154` 決定 6 が**補正 2 口を足すのと同じ判断の中で明示的に据え置いている**
   （「ワーカー自身に認可を課さない点も変えない」）。後から気付かれていない漏れではない。
2. **代償統制が 3 枚ある。** ① mesh STRICT mTLS ② ネットワーク分離（`NetworkIsolationTests` の
   `conversion-service` 行 ＋ Helm ClusterIP 検査） ③ BFF の門 —— 5 口すべてが
   `/bff/conversion/jobs` 経由で admin/operator に絞られ、retry と figure 系 3 口は `AdminOnly` が積まれている。
3. **利用者文脈を持たないワーカーである。** ロール門を積むには呼び出し元（BFF）からの
   トークン伝播が要るが、それは east-west に利用者トークンを流す向きであり `IADR-0379` 決定 4 に反する。

🔴 **ただし残差を明記する**: `ConversionService` は既に**自分の realm 資格情報を持っている**
（`ServiceToken__ClientId: conversion-service` が compose に、`externalsecret-conversion-service-token.yaml` が
ESO にある）。**つまり `ServiceCaller` を積む材料は既に配備されている。** 積んでいないのは
「まだ gRPC 面を持たないから」であって、原理的な障害ではない。**東西 gRPC 移行が
`ConversionService` に及んだ時点で、形は N から S へ動く**（`IADR-0379` フォローアップ 1 の 31 本に含まれる）。

### 決定 5: **`AddPlatformAuth` を 2 サービスへ足すことはしない**

決定 3・4 の帰結である。足しても `FallbackPolicy` が無い以上**1 つの口も塞がらず**、
「対処した」という誤った印象だけが残る。🔴 **見かけの指標（13/15 → 15/15）を満たすための変更を入れない。**

`IADR-0044` フォローアップ 1（`ConversionService` への認可）は、**本 IADR 決定 4 をもって
「据え置きを再確認した」として決着させる** —— 新しい作業ではなく、判断の再確認が答えである。

### 決定 6: 機械検査は**新設しない**。ただし**既存検査器の母集合の穴は塞ぐ**

🔴 **この 2 つは別の行為である。混ぜない。**

**新設しない**: 棚卸しは「`ingestion-service` が `NetworkIsolationTests` の列挙に無いのは
`conversion-service` に続く同型の事故 2 回目だ」と読んでいたが、**これは誤りである。**
`NetworkIsolationTests.cs:42-46` が除外を**理由つきの意図的判断として記録**しており、その理由
（サーフェス 1 件・副作用なし）は今も実測で成り立つ。→ **「同型の事故 2 回目」の条件はこの筋では
満たされない。この筋を根拠に検査器を新設しない**（`CLAUDE.md`「1 回目は記録に留める」）。

**穴は塞ぐ**: 母集合を記憶ではなく compose 側から引き直したところ（`traceability.repo.md` 規則 9）、
**別の筋で本物の漏れが 2 件見つかった**。compose の第一者アプリサービス（`build:` を持つもの）は 19 本、
現行の列挙は 14 本、host 公開する縁は 2 本（`bff` / `frontend`）。差の 3 本が
**`graph-service` / `mcp-service` / `ingestion-service`** である。前 2 者は**実態としては正しく
`expose:` のみ**（Helm も ClusterIP。`ingress:` を持つのは wikijs だけ）だが、**列挙に無いので
`ports:` への回帰を誰も止められない** —— `conversion-service` のときと同じ形の欠陥の 2 回目・3 回目である。

したがって:

1. 3 本を `InternalAppServices` へ足す。`NetworkIsolationTests.cs:46` 自身が
   「**追加するときはここへ 1 行足す**」と書いており、その指示に従う。
2. 🔴 **列挙を compose 側から検算する `[Fact]` を足す。** `build:` を持つ compose サービスは
   「内部（列挙）」か「host 公開の縁（allowlist）」の**どちらかに必ず属する**。どちらでもないものが
   現れたら落ちる。これは新しい統制ではなく、**既存検査器の既定を fail-open から fail-closed へ
   裏返す**ものである（今は列挙し忘れると黙って通る）。

### 決定 7: mTLS —— **宣言側に掃除すべき PERMISSIVE は残っていない。残りはクラスタでしか測れない**

実測（`deploy` 配下の `mtlsMode:` 値の全件）:

```console
$ git grep -n "mtlsMode" -- deploy
deploy/helm/microservices-platform/values.yaml:74:  mtlsMode: STRICT
deploy/helm/.../templates/istio-mtls.yaml:19,72:    mode: {{ .Values.mesh.mtlsMode }}
$ git grep -n -E "mtlsMode:\s*(PERMISSIVE|DISABLE)" -- deploy
（0 件。陽性対照: `mtlsMode: STRICT` は当たる）
```

宣言側に残る PERMISSIVE は 3 つだけで、**いずれも掃除の対象ではない**:

| 箇所 | 何か | なぜ残すか |
| --- | --- | --- |
| `istio-mtls.yaml:75` の `portLevelMtls` | backchannel logout の 1 URI | 本番 values は `fromOutsideMesh: false` なので**本番像では描画されない**。`DENY` の `AuthorizationPolicy` と対で機能する |
| `k8s-local-up.sh:313` の既定 | ローカル起動の既定 | `IADR-0377` 決定 3。AST がメッシュ外テナントであり STRICT が AST→MSP を切る（同 IADR の実測） |
| `istio-edge-down.sh:32` | ロールバック | 切り戻し経路 |

回帰は `MeshMtlsTests.cs:47,51-52` が両方向（STRICT の存在・PERMISSIVE/DISABLE の不在）で固定している。

🔴 **リポジトリ内で測れないもの**: 稼働 `PeerAuthentication` の実値と、`.spec` の**書き手**
（`managedFields`）。`IADR-0377` 決定 5 の G12 がこれを見るが、**live object を要するので原理的に静的検査にできない**。

🔴 **実測された穴（本 PR では埋めない）**: `git grep -n "ISTIO" -- .github` → **0 件**
（陽性対照: `LOCALEDGE` は 4 件）。**G12 の STRICT ドリフト検出は CI で一度も動いていない。**
埋めるにはメッシュ有効なクラスタを CI で立てる必要があり、本 PR の射程外。**展開 issue へ送る。**

### 決定 8: Vault —— **抽象は「作らない」。移行は配備層で完結させる**

実測: `ISecretStore` / `VaultSharp` はアプリコードに **0 件**
（陽性対照: 同一走査で `IObjectStorageClient` は **38 ファイル**。走査は生きている）。

**しかしこれを「欠落」と読むのは早い。** 現行の経路は既に成立している:

**`Vault → ExternalSecret(ESO) → k8s Secret → 非 optional な secretKeyRef → env → IOptions<T>`**

ESO マニフェストは **25 本**（`deploy/local/vault/eso/`）で、うち **8 本は s2s トークン**である
（aianalysis / conversion / datasource / graph / ingestion / mcp-server / retrieval / wiki）。
`check-secret-injected-options.js` が「k8s Secret から注入すると宣言した構成値」が helm と compose の
**両方**で実際に注入されていることを強制し、**母集合 0 件を緑にしない**（fail-closed）。

| | A. `ISecretStore` を作りアプリが実行時に Vault を引く | B. 配備層で完結させる（**採用**） |
| --- | --- | --- |
| アプリの起動時失敗 | 🔴 Vault 不達で**起動後に**縮退（静かに壊れる） | 非 optional な `secretKeyRef` で**Pod が起動しない**（大きな音で壊れる） |
| ローテーション | アプリが再取得できる | ESO の `refreshInterval: 1h` ＋ Pod 再起動 |
| アプリの依存 | 🔴 `VaultSharp` が全サービスへ入る | 0 |
| 12-factor | 🔴 反する（設定を実行時に引く） | 従う |
| テスト容易性 | モックが要る | env を置くだけ |

🔴 **したがって `ISecretStore` は作らない。** 0 件は欠落ではなく**設計の帰結**である。
`IObjectStorageClient` が 38 件あるのは、オブジェクトストレージが**実行時に何度も引く対象**だからであり、
シークレットは**起動時に 1 度だけ材料化される**。同型ではない。

**残っている実作業は 1 つだけである**: 🔴 **コネクタ資格情報**（#447 から委譲）。これは他と性質が違う ——
**利用者が画面から入れる値**であり、起動時に materialize できない。現状は
`DataSourceDbContext` が `jsonb` で**平文保存**し、応答のマスク（`SecretConfigMask` / `SecretMask`）だけで守っている
（`DataSourceService` 内の `Vault` 4 ヒットはすべて「Vault 移行までの暫定」というコメント）。

**移行段（設計のみ。本 PR では実装しない）**:

1. 書き込み時に値そのものではなく **Vault の参照（`vault:msp/datasource/<id>#<key>`）**を `jsonb` へ入れる。
2. 読み出しは**コネクタ実行時にのみ**解決する（`IConnectorSecretResolver`。`ISecretStore` のような
   汎用抽象は作らない —— **必要なのはこの 1 用途だけ**である）。
3. 既存行の移送と、参照が解決できないときの fail-closed。
4. ローテーションは Vault 側の版管理に委ね、アプリは参照だけを持つ。

**実配備は環境依存であり本 PR では検証できない。展開 issue へ送る。**

## 結果

- 良い影響:
  - `IADR-0017` 決定 3 が前提の交代に追随して言い直され、**「内部サービスに JWT が無い」の意味が確定した**。
  - `IADR-0044` フォローアップ 1 が 2 か月ぶりに決着した（据え置きの再確認として）。
  - `NetworkIsolationTests` の母集合が 14 → 17 になり、**既定が fail-open から fail-closed へ裏返った**。
- 悪い影響・トレードオフ:
  - 🔴 **`NFR-09`（全 API で OIDC/JWT）の文面は、決定 3 の判定表では満たされない。**
    本 IADR は「口の性質ごとに層を選ぶ」ことを選び、**文面との差を残差として明示する**道を採った。
    残差は 3 つ: `AuthorizationService` REST `/authz/scope`・`LlmGateway` REST 3 口・`ConversionService` 5 口。
    **いずれも east-west gRPC 移行（`IADR-0379` フォローアップ 1 の 31 本）が及べば S へ動く。**
    🔴 **この差の扱い（計画 NFR-09 の文面を口の単位へ改めるか、実装を文面へ寄せるか）は
    実装側で決めてよい範囲を超える。計画へ裁定を依頼する**（下記「計画への環流」）。
  - G12 が CI で動いていない穴は本 PR では埋まらない。
- フォローアップ（**いずれも本 PR の外**）:
  1. コネクタ資格情報の Vault 参照化（決定 8 の 4 段）。着地すれば **#447 も閉じられる**。
  2. CI でメッシュ有効なクラスタを立て、G12 を実際に走らせる（決定 7）。
  3. east-west gRPC の残り 31 本の展開に伴い、決定 3 の残差 3 件が S へ動くことを確認する。

## 計画への環流

**`NFR-09`「全 API で OIDC/JWT 認証」の判定単位を裁定してもらう必要がある。**
本 IADR 決定 3 は「サービス単位で認証基盤の有無を数える」読みを退け、「口の性質ごとに層を選ぶ」を採ったが、
**それが `NFR-09` の充足と言えるかは計画側の裁定事項**である（`traceability.repo.md`:
「無いことは『実装側で作ってよい』ではない」と同じ向きの制約）。`/plan-feedback` で
`decision-needed` ラベルつきの issue を起票する。**起票前に同件の既存 issue を検索すること。**

## 関連

- Supersedes: なし（`IADR-0017` は既に `IADR-0026` が Superseded 済み。本 IADR はその**帰結を口の単位へ下ろす**もので、
  新たに何かを Superseded にはしない）
- Superseded by: なし
