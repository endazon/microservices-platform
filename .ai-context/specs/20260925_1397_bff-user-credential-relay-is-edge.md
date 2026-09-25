---
title: "BFF → 各サービスの利用者資格情報を運ぶ 15 本を east-west に数えない —— オーナー裁定の記録と数え方の追随（#1397）"
type: spec
status: in-progress
related_ids: [NFR-09, NFR-16, ADR-0029, ADR-0032, ADR-0075, ADR-0086, ADR-0089, IADR-0379, IADR-0401, IADR-0402, IADR-0426, IADR-0458]
author: claude
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md §決定（該当経路に「BFF → 各サービス」を挙げる）
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 3・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md 決定 1・3
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-09 / NFR-16
---

# 仕様書: BFF の利用者資格情報の中継は east-west ではない（#1397）

## 起点となる計画書（トレーサビリティ）

- 非機能要件: `NFR-09`（全 API で OIDC/JWT 認証。暫定条項は「エッジ（BFF）で担保」）／`NFR-16`（サービス間 mTLS）
- 関連 ADR: `ADR-0029`（east-west 同期は gRPC）／`ADR-0075` 決定 3・5・6（一括移行・実装側 IADR で REST 継続を自認しない・基盤先行は MSP 自身を含む）／`ADR-0086` 決定 1・3（利用者文脈は本文で運ぶ・対象 2 経路）／`ADR-0089` 決定 1（REST 並走の経路は退役をもって解けたと数える）／`ADR-0032`（BFF セッション方式）
- 関連 IADR: `IADR-0379` 決定 4（north-south = 利用者トークン／east-west = s2s）／`IADR-0401`・`IADR-0402`（BFF の 15 本を数えた先行記録）／`IADR-0426`（「扱いは未定」と書いた記録）／**`IADR-0458`（本作業で起こす。事前割り当て）**
- 起票: #1397（#1255 の 2026-09-10 コメントと 2026-09-11 監査が「扱いが未定」とした項目）
- **オーナー裁定（2026-09-25・#1397 コメント）**: 「BFF はエッジであり、BFF → 各サービスの利用者資格情報を運ぶ 15 本は east-west（ADR-0029 の gRPC 化の対象）に含めない。IADR に記録し、ADR-0029 / ADR-0086 の扱いとして計画へ環流する。」
- 同日の #1255 裁定: ④⑤（MCP ツール申告・introspection の扇形）は全宛先に gRPC の口を実装して移す。BFF → サービスの 15 本は #1397 の裁定により対象外。

## 目的・背景

#1255 の受け入れ基準「east-west の `AddHttpClient` が 0 本」を満たせない主因が BFF の 15 本になっていた。帰属が決まらないまま #1255 の残作業（④⑤・REST 退役）を進めても 0 にならない。オーナーが帰属を裁定したので、それを IADR に記録し、east-west の残りを数える文書を裁定と整合させる。

## 🔴 計画 ADR との関係（着手前に確認した制約）

- **`ADR-0029` §決定は「現行の該当経路」に「BFF → 各サービス」を挙げている。** `ADR-0075` 決定 6 もそれを引いて「基盤先行は MSP 自身の移行を含む」としている。本裁定は**計画 ADR の文面と食い違う**。
- **`ADR-0029` は例外を「対象経路を明記した新 ADR」に限り、`ADR-0075` 決定 5 は「実装側 IADR で REST 継続を自認しない」とする。** したがって本 IADR は**実装側の判断として REST 継続を自認するものではない**。記録するのは**両 ADR の決定者（利用者＝オーナー）の裁定**であり、計画 ADR の文面への反映（新 ADR か改定か）は計画側の判断に委ねる（環流の下書きを報告に添える。起票は本作業では行わない）。
- 本作業は**コードを 1 行も変えない**。数え方を記録し、数を書く文書を追随させるだけである。

## 母集合（着手時に自分で引いた。#1255 本文の表は転記しない）

基点 `origin/develop` `d3e8b2a4`。`git rev-parse --is-shallow-repository` = `false`。AST submodule の pin は `471cbf31`（`git ls-tree HEAD src/ai-stock-trading`）で、AST の BFF モジュールは隣接クローンの同 SHA を `git grep` で読んだ（本 worktree では submodule が未展開）。

### 軸 1: BFF の登録（`AddHttpClient` の名前付きクライアント。`src/platform/backend/Bff/Platform.Bff/Program.cs`）

| # | 名前付きクライアント | 利用者の資格情報を後段へ付ける呼び出し箇所（`CreateClient(` → `Authorization` 付与） | 付けない呼び出し箇所 |
| --- | --- | --- | --- |
| 1 | `AiAnalysisService` | `AnalysisBffEndpoints` 3 | — |
| 2 | `FeedbackService` | `FeedbackBffEndpoints` 2・`DashboardBffEndpoints` 1 | — |
| 3 | `DashboardService` | `DashboardBffEndpoints` 1・`UsageEventDispatcher` 1（列に載せた `signal.Authorization`） | — |
| 4 | `AuthorizationService`（管理面の代理） | `AuthzBffEndpoints` 1・`UserAdminBffEndpoints.Proxy` 1（利用者管理・利用者検索・グループ検索が共有） | — |
| 5 | `RetrievalService` | `SearchBffEndpoints` 2（検索・属性値照会の REST 側） | — |
| 6 | `GraphService` | `GraphBffEndpoints` 2・`EdgeTypeDictionaryBffEndpoints` 1 | — |
| 7 | `McpServer` | `McpClientBffEndpoints` 1 | — |
| 8 | `NotificationService` | `NotificationBffEndpoints` 1 | — |
| 9 | `WikiService` | `WikiBffEndpoints` 1 | — |
| 10 | `DocumentService` | `DocumentBffEndpoints.Forwarding` 1・`PrivateNoteBffEndpoints` 1・`SearchBffEndpoints` 1・`TagDictionaryBffEndpoints` 1 | `DocumentBffEndpoints` の読み取り 4（gRPC `DocumentRead` の REST 並走側） |
| 11 | `ConversionService` | `ConversionBffEndpoints` 1 | — |
| 12 | `DataSourceService` | `DataSourceBffEndpoints` 1 | — |
| 13 | `ConfigurationService`（AST） | AST `AssumptionsBffEndpoints` 1 | — |
| 14 | `RiskManagementService`（AST） | AST `RiskControlsBffEndpoints` 1 | — |
| 15 | `MarketMonitorService`（AST） | AST `MonitorBffEndpoints` 1 | — |

**登録単位 15 本・利用者の資格情報を付ける呼び出し箇所 27（MSP のコード 24・AST submodule 3）。** 15 本すべてが少なくとも 1 箇所で利用者の資格情報を後段へ付ける。

### 軸 2: BFF が読む宛先の構成キー（`Services:<Name>`）

`git grep -o 'Services:[A-Za-z]+'`（`Platform.Bff`・`Knowledge.Bff`・`Platform.Shared.Infrastructure`）で REST の宛先キー **15**（上表と一致）＋ gRPC の宛先キー 3（`AuthorizationServiceGrpc` / `DocumentServiceGrpc` / `RetrievalServiceGrpc`）＋ `LlmGatewayGrpc`（Shared の登録関数。BFF は使わない）。**上表の外にある REST の宛先は無い。**

### 軸 3: 配備側の注入（`deploy/helm/.../values.yaml`・`deploy/docker-compose.yml` の `Services__*`）

上 2 軸に無い BFF の宛先は出ない（小文字の `Services__<svc>` は `Introspection__Services__*` の宛先集合）。

### 🔴 #1255 本文の内訳との食い違い（数は同じ 15、中身が 1 本違う）

| #1255 本文 | 実測 | 判定 |
| --- | --- | --- |
| 名前付き 14（`AuthorizationService` を含まない） | 名前付き **15**（`AuthorizationService` の管理面の代理を含む） | #1255 本文が落としていた。`IADR-0401` §残るもの・`IADR-0402` §母集合は「15 本（`UserAdminBffEndpoints` を含む）」と数えており、そちらと一致する |
| ＋ `HttpEffectiveConfigCollector` 1 | **利用者の資格情報を運ばない**（`Authorization` を 1 度も付けない。`HttpEffectiveConfigCollector.cs`） | **15 本に入らない。** これは #1255 の ⑤（introspection の扇形）そのものであり、同日の #1255 裁定で**gRPC へ移す側**に入った |

### 除外したものと理由（規則 6）

| 除外 | 理由 |
| --- | --- |
| `AuthzScopeHttpClient`（BFF → `/authz/scope`） | **BFF 自身の s2s** で呼ぶ。east-west のまま（参照実装 gRPC の REST 並走側。退役は `ADR-0089` 決定 1 の規則で数える） |
| gRPC クライアント 3（`AuthzScope` / `DocumentRead` / `AttributeValues`） | s2s ＋ 本文の利用者文脈。east-west のまま（移行済み・並走中） |
| `DocumentService` の読み取り 4 箇所（REST） | 資格情報を付けない。gRPC `DocumentRead` の REST 並走側であり、east-west の退役規則で数える。**名前付きクライアント `DocumentService` 自体は書き込み側で資格情報を運ぶので 15 本に入る** |
| `SearchBffEndpoints` の属性値照会（REST 側・資格情報あり） | 15 本（`RetrievalService`）の呼び出し箇所に数えるが、**gRPC `AttributeValues` の REST 並走側でもある**。この呼び出し箇所が消えるのは当該経路の REST 退役による（本裁定ではない） |
| `HttpEffectiveConfigCollector` | 上表。⑤ として #1255 が移す |
| `OpendAuthGateway`（AST `OpendAuthBffEndpoints`） | 後段は**サービスではなく OpenD Pod のサイドカー**で、認証を持たず、**BFF が利用者の資格情報を付けない**（BFF が唯一の認可点）。`AddHttpClient` 登録も MSP 側に無い |
| `VaultKvClient` / `ExternalSecretSyncRequester` / `SessionTokenRefresher` / 既定の `AddHttpClient()` | 後段が Vault・ESO・IdP（Keycloak）であり、メッシュ内のサービスではない |
| サービス → サービスの呼び出し（AiAnalysis / Graph / Retrieval ほか） | 呼び出し元が BFF ではない。本裁定の射程外（#1255 のまま） |

## 裁定の読み方（本作業で固定する分類基準）

- **エッジ（本裁定で east-west から外すもの）**: **BFF が利用者の要求を中継し、利用者の資格情報を後段へ付ける呼び出し。** 後段はその資格情報で自分の門（`AdminOnly` ／ ABAC ／主体の絞り込み）を判定する。north-south の続きであり、`IADR-0379` 決定 4 の「north-south = 利用者トークン」と同じ線である。
- **east-west（従来どおり）**: **BFF 自身の資格情報（s2s）で呼ぶ呼び出し**と、サービス → サービスの呼び出し。
- 🔴 **この基準はオーナー裁定の文言（「利用者資格情報を運ぶ 15 本」）を、呼び出し箇所へ降ろしたものである。** 裁定を広げない —— 「BFF から出る呼び出しはすべてエッジ」とは読まない（s2s の 3 面と扇形 ⑤ は east-west に残る）。

## 実装方針（変更するもの）

| # | 対象 | 変更 |
| --- | --- | --- |
| 1 | `.ai-context/adr/IADR-0458_*.md`（新規） | 裁定・分類基準・15 本の列挙・#1255 本文との食い違い・計画 ADR との関係と環流 |
| 2 | `.ai-context/adr/README.md` | 索引へ 1 行 |
| 3 | `docs/api/east-west-grpc.md` | §概要「対象」・§4「BFF セッション方式との分け方」・§5 の判定表・§未決事項の残り内訳を裁定と整合させる（**表示テキストへ ID を書かない**。trace ブロックへ `IADR-0458` と #1397） |
| 4 | `docs/tech/tech-requirements.md` | 「残る east-west 31 本」（古い数）を現況へ |
| 5 | `.ai-context/adr/IADR-0426_*.md` | 「扱いは未定のままである」の隣へ日付つき追記（決定を変える追記ではなく、未決が決まった旨の指し先） |

### 追随する文書の母集合（規則 9: 誤りの側の文字列で引いた）

検索語（誤りの側）: `31 本` / `残 ?31` / `15 本` / `BFF ?(→|->) ?各サービス` / `利用者(の)?資格情報(を)?(運ぶ|搬送)` / `east-west.*残` / `残.*east-west` / `1397`。走査範囲は追跡下の全ファイル（`.ai-context/specs`・`.ai-context/superpowers`・`CHANGELOG.md`・submodule を除く）。**拡張子で絞っていない。**

| ヒット | 扱い |
| --- | --- |
| `docs/api/east-west-grpc.md` 652・655・665・670 | **追随する**（上表 3） |
| `docs/tech/tech-requirements.md` 444 | **追随する**（上表 4） |
| `IADR-0426` 145 | **追随する**（上表 5。未決と書いた live な状態文） |
| `IADR-0379` 151・160／`IADR-0403` 185・301・308／`IADR-0408` 78・83・224／`IADR-0412` 16・123／`IADR-0417` 15／`IADR-0419` 15 | **追随しない。** 各時点の基点で数えた**実測値の記録**であり、書き換えると追試できなくなる（`docs/api/east-west-grpc.md` 自身が「数え直しは基点ごとに行う」と定めている） |
| `IADR-0401` 225・238・248／`IADR-0402` 85・86 | **追随しない。** 数（15 本・28/23）は当時の実測であり、本裁定と矛盾しない（15 本の中身は本作業の実測と一致） |
| `deploy/docker-compose.yml` 923／`values.yaml` 995／`DocumentBffEndpoints.cs` 31 | **追随しない。** 「書き込み経路は利用者の資格情報を運ぶので移さない」—— 本裁定と同じ向きの記述であり、誤りにならない |
| その他の `15 本`（`IADR-0163` / `IADR-0228` / `IADR-0236` / `IADR-0330` / `IADR-0332` / `coverage-floor.json` / `local-sso-recovery-runbook.md`） | 別の事物の数（検査器・テスト・ExternalSecret ほか）。無関係 |

### 規則 10（この変更で新たに誤りになる自分の記述）

- `docs/api/east-west-grpc.md` §5 の判定表の「検索・グラフ・Wiki…ABAC 管理 | 移さない | 同上」は、理由が「資格情報を運ぶから（技術的に移せない）」のまま残ると、**本裁定後は「移さない理由」が変わる**（移す対象でない）。表の直後へ裁定後の読み方を 1 段足す。
- 同 §未決事項「BFF → 各サービスの利用者資格情報を運ぶ経路は…扱いは未定である」は**本作業で誤りになる**。日付つき追記で解消を書く（原文は実測の記録として残す）。

## 受け入れ基準（Given-When-Then）

- [ ] Given 本 PR / When `IADR-0458` を読む / Then 裁定・分類基準・15 本の列挙（名前付きクライアントと呼び出し箇所）・#1255 本文との食い違い・計画 ADR（`ADR-0029` §決定 / `ADR-0075` 決定 5・6 / `ADR-0086`）との関係が書かれている
- [ ] Given `docs/api/east-west-grpc.md` と `docs/tech/tech-requirements.md` / When east-west の残りを読む / Then BFF の 15 本が east-west の残りに数えられておらず、「扱いは未定」「残る 31 本」が残っていない（原文を残す箇所は日付つき追記で解消が示されている）
- [ ] Given `docs/` の変更 / When `node scripts/check-trace-blocks.js` / Then 緑（表示テキストへ ID を書いていない）
- [ ] Given 索引 / When `node scripts/scripts.repo.test.js` 系の索引検査 / Then 緑
- [ ] Given 本 PR / When コード差分を見る / Then `src/` の変更は 0（C# のビルド・テストは不要）

## 確かめないこと（射程外）

- #1255 の残作業（④⑤ の gRPC 化・REST 退役・`IADR-0379` 決定 5 の反転）。本裁定はその射程から 15 本を外すだけである。
- 計画 ADR の文面の改定（計画側の判断。環流の下書きを報告に添えるが起票しない）。
- `NFR-09` 恒久条項の経路ごとの達成の数え方への影響（BFF の後段は利用者トークンを自分で検証しており、エッジの中継は「全 API で OIDC/JWT 認証」と矛盾しない。記録に留める）。

［2026-09-25 追記 / #1397］**実装 ADR の番号を `IADR-0462` から `IADR-0458` へ改番した。** 事前割り当ての 0458〜0461 が
どの PR にも使われず欠番になり、`check-adr-numbering` が止めたためである（develop の最大は `IADR-0457`）。
本書・索引・trace ブロック・`IADR-0426` の追記はすべて `IADR-0458` に追随させた。
🔴 **PR は #1501 から出し直した。** #1501 のブランチには件名のスコープに `IADR-0462` を持つコミットが残り、
改番後は `check-commit-messages` の実在性検査（必須 check）が必ず落ちる。履歴を書き換えずに範囲から外す手は
無い（`commit-allowlist.json` の区分 B は統合ブランチ上のコミットに限られる）ため、develop から切った新しい
ブランチへ変更を 1 コミットで載せ直した。#1501 のブランチは残してある。
