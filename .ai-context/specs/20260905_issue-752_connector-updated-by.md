---
title: コネクタ 3 実装が更新者を運ぶ（wiki / saas は自前 DTO、db は自前 SQL）（#752）
type: spec
status: done
related_ids: [FR-05, UC-04, ADR-0036, ADR-0074, IADR-0051, IADR-0053, IADR-0054, IADR-0055, IADR-0392]
author: Claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0074_owner-mapping-table-container-in-sc06.md (決定 3・5 / §残るもの)
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md (§システム投入経路)
---

# #752: コネクタ契約が更新者を運ばないため取り込み経路で所有者を決められない

## 0. 🔴 着手前の訂正 —— `blocked` は解けている（自分で測り直した）

本 issue は長らく `blocked:env` + `blocked:human` だった。**その前提を、記述を信じずに測り直した。**
基点は `origin/develop` `25b9485a`。

```console
$ git rev-parse --is-shallow-repository
false
```

| 従前の待ち | 実測（2026-09-05） |
| --- | --- |
| planning#518 の裁定待ち | `gh api repos/endazon/project-planning/issues/518` → `state=closed` / `closed_at=2026-09-03T00:58:44Z`。計画 `ADR-0074` が `status: Accepted` |
| 器・解決器が無い | `gh issue view 1194` → `CLOSED` / `COMPLETED` / `2026-09-03T10:49:55Z`。`DataSource.ResolveOwner`（`Domain/DataSource.cs:334`）と `DataSourceSyncService.cs:59` の `source.ResolveOwner(item.UpdatedBy)` が実在する |
| 受け入れ観点「`owner=system` の件数が減る」の実測に稼働クラスタが要る | **基準そのものが撤回されている。** `ADR-0074` 決定 3 が「器を入れても予約値は減らない」「件数を環流債務として読まない」と裁定した |
| ① Keycloak 検索の配備 | **本 issue の射程外である。** 解決順は ① → ② → 予約値であり、② だけで解決順は閉じる（`ADR-0074` 決定 1・`DataSourceSyncService` の注記） |

**🔴 依頼文の言い回しへの訂正**: 撤回されたのは**受け入れ観点の方**であり、`ADR-0074` **決定 3 自体は生きている**
（決定 3 が観点を撤回した側である）。本仕様書は決定 3 を**生きている根拠として**使う。

**`ADR-0074` 決定 5**（`db` へ更新者列を載せるのは解決器の配備後）の前提条件は `#1194` で満たされた。
したがって `db` への搭載は**いま行ってよい**。

## 1. 母集合（規則 9。走査して確定した。陽性対照つき）

```console
$ git grep -n "record SourceItem" -- src/
src/knowledge/backend/Services/DataSourceService/Domain/Ports/IDataSourceConnector.cs:47:public sealed record SourceItem(string Path, DateTimeOffset ModifiedAt, long Size, string? UpdatedBy = null);

$ git grep -n "new SourceItem" -- src/ | grep -v /Tests/     # 製品コード
.../Infrastructure/ExternalServices/DatabaseConnector.cs:70:   items.Add(new SourceItem(id, updated, 0));
.../Infrastructure/ExternalServices/FileSystemConnector.cs:83: items.Add(new SourceItem(file, modifiedAt, info.Length));
.../Infrastructure/ExternalServices/SaaSConnector.cs:66:       items.Add(new SourceItem(it.Id, it.UpdatedAt, 0));
.../Infrastructure/ExternalServices/WikiConnector.cs:57:       items.Add(new SourceItem(page.Id, page.UpdatedAt, 0));

$ git grep -n "UpdatedBy" -- src/knowledge/.../Infrastructure/ExternalServices/ | wc -l
0                                    # ← 陰性
$ git grep -n "SourceItem" -- src/ | wc -l
58                                   # ← 陽性対照（走査自体は当たっている）
```

**製品コードで `SourceItem` を作っているのは 4 実装、`UpdatedBy` を運んでいるのは 0 件。**
`IDataSourceConnector` の実装は `ConnectorRegistry` に登録されたこの 4 本で全部であり、
テスト内のスタブ（12 箇所）は母集合ではない（測っているのは同期側の配線であって取得元ではない）。

### 各コネクタの現況と、更新者をどこから取るか

| コネクタ | いま持っているメタ | 更新者の取得元 | 本 PR の扱い |
| --- | --- | --- | --- |
| `filesystem` | `FileInfo`（パス・`LastWriteTimeUtc`・`Length`） | **無い。** Linux でファイル所有者を取る自明な手段が無く、そもそも「ファイル所有者」は「最終更新者」ではない | 🔴 **`UpdatedBy: null` を明示的に運ぶ**（`ADR-0074` 決定 3。**構造上運べないことは欠陥ではない**） |
| `wiki` | 自前 DTO `WikiPage(Id, Title, UpdatedAt)` | **自前 DTO が読む項目を増やせば取れる。** 接続先は `ConnectionUri` ＋ `Config["listPath"]` で構成可能な汎用 JSON エンドポイントであり、外部組織が持つ契約ではない | **構成可能な項目名**（`Config["updatedByField"]`。既定 `updatedBy`）で受ける |
| `saas` | 自前 DTO `SaaSItem(Id, Title, UpdatedAt)` | 同上 | 同上 |
| `db` | `SELECT id, updated FROM ( {query} ) AS src` | **管理者が書くクエリの列。** 自前 SQL なので列を 1 本足せる | 🔴 **opt-in の `Config["updatedByColumn"]`。** 無条件に列を足すと、その別名を持たない既存クエリが**全件 SQL エラー**になる |

## 2. 🔴 「取れなかった」と「取ったら空だった」を混ぜない

`SourceItem.UpdatedBy` は `string?` の 1 本であり、**どちらも最終的には `null`** →
`ResolveOwner(null)` → 予約値 `system` へ落ちる。**落ち方が同じでも、由来を潰さない。**

由来は `SourceUpdatedBy`（純関数）が 4 値に分類し、`Discover` の 1 サイクルにつき 1 行だけ集計を記録する。

| 由来 | 意味 | 記録 |
| --- | --- | --- |
| `NotCarried` | 項目・列がそもそも無い（構成されていない／ソースが返さない） | 集計のみ（既定の状態であり異常ではない） |
| `BlankAtSource` | **項目は在ったが値が空だった**（空文字・空白のみ・SQL `NULL`） | 集計＋警告（ソース側のデータ不備であり、構成の不備ではない） |
| `Unreadable` | 項目は在ったが**文字列として読めない**（JSON のオブジェクト・配列・数値等） | 集計＋警告（**構成した項目名が別物を指している**兆候） |
| `Carried` | 値が取れた | `SourceItem.UpdatedBy` に載る |

**`Carried` でも `owner` になるとは限らない** —— `ResolveOwner` は写像表の完全一致（`Ordinal`）であり、
当たらなければ `null` を返す。**生の識別子は 1 件も `owner` に入らない**（`ADR-0036` /
09_datasource-connectors「推測で埋めない」「安全側は『解決しない』」）。

## 3. 破壊的変更の扱い

| 変更 | 分類 | 根拠 |
| --- | --- | --- |
| `SourceItem` への項目追加 | **無し。** `UpdatedBy` は 2026-08-21 の段 1 で既に在る | `git grep "record SourceItem"`（§1） |
| `Knowledge.Contracts` / `Shared.Contracts` | **触らない** | `RawDocumentFetched.Attributes` は既存の辞書であり、入る値が増えるだけで形は変わらない |
| `wiki` / `saas` の JSON 契約 | **非破壊（加算のみ）。** 項目が無ければ `NotCarried` | 既定 `updatedBy` は**未知項目**として現在も無視されている（`JsonSerializerDefaults.Web`） |
| `db` の SQL 契約 | 🔴 **opt-in にすることで非破壊。** `Config["updatedByColumn"]` 未設定なら発行 SQL は 1 文字も変わらない | 無条件に足すと既存クエリが全件失敗する（＝破壊的） |

`node scripts/check-contract-schema.js` は `Shared.Contracts` 等の契約型を見る検査であり、
`scripts/contract-schema-baseline.json` に `SourceItem` / `IDataSourceConnector` は**1 件も無い**
（`grep` で 0 件・陽性対照として `DataSourceSyncHealth` は在る）。**差分が出ないことを実行して確かめる。**

## 4. やること

1. `Infrastructure/ExternalServices/SourceUpdatedBy.cs` を新設（4 値分類の純関数。JSON 由来と DB 由来の 2 入口）。
2. `WikiConnector` / `SaaSConnector`: DTO へ `[JsonExtensionData]` を足し、構成された項目名で引く。
3. `DatabaseConnector`: `Config["updatedByColumn"]` が在るときだけ SELECT へ足す。**識別子は正規表現で検証**し、
   通らなければ**未設定として扱い警告する**（不正な文字列を SQL へ差し込まない）。
4. `FileSystemConnector`: `UpdatedBy: null` を名前付き引数で明示する。
5. テスト（xUnit）: 陽性・陰性・変異試験。
6. `IADR-0392` ＋ 索引、`docs/functional/FR-05` / `docs/screens/SC-06` の追随。

## 5. 受け入れ基準（Given-When-Then）

- [x] Given `updatedBy` を返す wiki / saas / Given `updatedByColumn` を構成した db / When `Discover` する
      / Then `SourceItem.UpdatedBy` に**生の値**が載る
- [x] Given 更新者を運ぶソースと、その識別子を含む写像表 / When 同期する / Then `owner` が**写像先の利用者**になる
- [x] Given `filesystem` / When `Discover` する / Then `UpdatedBy` は **null のまま**で、`owner` は予約値 `system`
- [x] Given 写像表に当たらない更新者 / When 同期する / Then `owner` は予約値 `system` であり、
      **生の識別子は入らない**。**他アイテムの更新者も混入しない**
- [x] Given `updatedByColumn` 未設定の db / When `Discover` する / Then 発行 SQL は従前と**同一**
- [x] Given 更新者の受け渡しを外した実装 / When 上の陰性試験を回す / Then **落ちる**（変異試験）
- [x] 🔴 `owner=system` の件数は**完了判定に使わない**（`ADR-0074` 決定 3）

## 6. 測らないもの

- 稼働クラスタでの実接続（`ADR-0074` 決定 3 が観点を撤回済み。本 issue は in-repo で閉じる）。
- ① Keycloak ユーザー検索（未配備。解決順は ② だけで閉じており、本 issue の射程外）。
- 実 Wiki / SaaS 製品の項目名（**構成可能にした**ことで、製品ごとの写像は運用時に決まる）。

## 7. 実測（証跡）

```console
$ dotnet test Services/DataSourceService/Tests/DataSourceService.Tests.csproj
成功!   -失敗:     0、合格:   234、スキップ:     0、合計:   234

# 変異試験: WikiConnector の受け渡しだけを外す
#   items.Add(new SourceItem(page.Id, page.UpdatedAt, 0, updatedBy.Value))
#   → items.Add(new SourceItem(page.Id, page.UpdatedAt, 0))
$ dotnet test ...
失敗!   -失敗:     4、合格:   230、スキップ:     0、合計:   234
  失敗 ConnectorUpdatedByOwnerResolutionTests.Sync_WithARealWikiConnector_ResolvesOwnerFromTheSourceUpdater
  失敗 ConnectorUpdatedByOwnerResolutionTests.Mutation_DroppingTheUpdatedByPassThrough_BreaksTheOwnerResolution
  失敗 WikiConnectorTests.Discover_UsesConfiguredUpdatedByField
  失敗 WikiConnectorTests.Discover_CarriesUpdatedBy_FromTheDefaultField
# 戻すと 234 件が緑に戻る（＝陽性は空の主張ではない）

$ dotnet format backend.slnx --verify-no-changes      # 差分なし
$ node scripts/check-contract-schema.js
[check-contract-schema] OK: 2 プロジェクト / 32 ファイル / 111 型が baseline と一致（未消化の承認 0 件）。
$ node scripts/check-openapi-dto-drift.js             # OK
$ node scripts/check-trace-blocks.js                  # OK: 169 件
$ node scripts/check-doc-links.js                     # OK: 1167 件
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js   # 732 tests passed（採番を詰めた状態で実測）
```

🔴 **`check-adr-numbering` は `IADR-0379`〜`IADR-0391` を欠番として赤にする。**
本 IADR は採番衝突を避けるため `IADR-0392` を仮に取っており、**改番はマージ時に行う**。
連番を詰めた状態で全体を回して 732 件緑を確認済みであり、**欠番以外の違反は無い**。

**`src/platform/backend` はこのワークツリーではビルドできない** —— submodule
`src/ai-stock-trading` が未 populate（`git submodule status` が `-` 前置）で `Platform.Bff` が
参照を解決できないためである。**本変更は platform を 1 行も触っていない**（変更は
`src/knowledge/backend/Services/DataSourceService/**` と文書のみ）。CI で検証する。

## 8. 運用上の残り（本 PR では触らない）

`updatedByField` / `updatedByColumn` は `Config` 辞書のキーであり、**SC-06 のフォームには
コネクタ設定の汎用編集欄が無い**（現状フォームが持つのは名前・種別・接続先 URI・既定属性 3 つ・写像表）。
したがって当面は API（`config`）から設定する。**画面側は PR #1260 が占有中のため触らない。**
