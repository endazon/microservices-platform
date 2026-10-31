---
title: IADR-0666 構成取得 API の「コネクタ一覧」（SC-06 に登録したデータソースと有効・無効）は、既存の connectors に混ぜず dataSources という別の項目で、データソースサービスの要求のたびの寄与として載せる（#2285 の (b)(ii)）
type: impl-adr
status: Accepted
related_ids: [FR-15, FR-01, SC-06, SC-11, ADR-0018, IADR-0029, IADR-0122, IADR-0660]
author: claude
created: 2026-10-31
updated: 2026-10-31
related_specs:
  - ../specs/20261031_2285_introspection-registered-datasources.md
---

# IADR-0666: 構成取得 API の登録済みのデータソース（#2285 の (b)(ii)）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-31
- 決定者: claude（#2285）。着手はコーディネーターの指示（2026-10-31）による。

## 起点・関連

- 起点 issue: #2285（(b)(ii)。(d-2) は #2368・IADR-0660 で済み）。
- 計画:
  - `10_composability-design.md` §6 の応答項目の表。planning#850 の利用者裁定（2026-10-30）は (ii) である。
    - 「コネクタ一覧」は、SC-06 に登録したデータソースと有効・無効を指す。
    - 「コネクタの種別」（プラグイン 4 種。常に有効）は、別の項目として残る。
  - ADR-0018。
- 前提:
  - IADR-0029（自己申告。フォローアップ 2）。
  - IADR-0660（要求のたびの寄与 `IIntrospectionContributor`）。
  - IADR-0122（契約は非破壊の追加）。

## コンテキストと課題

- 既存の `connectors`（proto `connectors = 4`・`ConnectorDto(Name, Enabled)`）は、データソースサービスが起動時に申告する**プラグインの種別**である（#2286）。
  - 実効構成は、それを名前で一意化している。
  - SC-11 は「コネクタ」の表で出していた。
- IADR-0660 は「(b)(ii) は DataSourceService がコネクタを寄与する形で足せる」と書いた。
- しかし計画は、「コネクタ一覧」と「コネクタの種別」を**別の項目**に書き分けた。
- データソースは実行時に増減する（DB の `DataSources`）。起動時の申告には載せられない。

## 決定

1. **別の項目 `dataSources` として足す。`connectors` に混ぜない。**
   - DTO `DataSourceSummaryDto(Id, Name, Type, Enabled)`。
   - proto `repeated DataSource data_sources = 6`。
   - `ServiceIntrospectionDto` と `EffectiveConfigDto` の末尾に、既定値つきで足す。
   - openapi では `required` に足す（応答は常に配列）。
   - 契約の対応は次のとおりである。
     - `dataSources` が、計画の「コネクタ一覧」に当たる。
     - 既存の `connectors` が、計画の「コネクタの種別」に当たる。
   - 混ぜない理由は 3 つある。
     - 計画が別の項目にした。
     - `connectors` の意味を変えると、破壊的な変更になる。
     - 名前で一意化している集約に入れると、同名のデータソースが 1 件に潰れる。
2. **寄与の器に `DataSources` を足す**（`IntrospectionContribution` の末尾・既定値つき）。`ApplyAsync` は静的な申告の後に足す。
3. **データソースサービスの寄与** `RegisteredDataSourcesIntrospectionContributor`。
   - 要求のたびにスコープを作って DB を読む。
   - 状態に依らず全行を、名前→ID の順に載せる。`enabled` は状態が `active` のときに真とする。
   - 🔴 ID・名前・種別・有効だけを出す。接続先・設定・資格情報は出さない（DTO の形を試験で固定する）。
   - 🔴 DB の例外は握り潰さない（IADR-0660 のとおり）。gRPC の失敗になり、BFF はデータソースサービスを「到達不能」にする。
4. **集約**: ID で一意化し、名前→ID の順に並べる。ドリフト検出には使わない（実行時データで、Git の宣言に無い）。
5. **SC-11**:
   - 「データソース（コネクタ一覧）」の表（名前・種別・状態）を足す。0 件は空の状態で出す。
   - 既存の表は「コネクタの種別」と表記を改める（プラグインは常に有効と書く）。

## 却下した案

- **`connectors` へデータソースを寄与する**（IADR-0660 の当初の想定）: 計画の書き分けに反する。種別とデータソースが 1 つの表で区別できなくなり、同名が潰れる。
- **`connectors` の中身をデータソースへ置き換え、種別を新しい項目へ移す**: 既存の項目の意味が変わる（破壊的な変更）。古い BFF と新しいサービスが混在する間に、表示が入れ替わる。
- **BFF が DataSourceService の REST（一覧）を直接読む**: 自己申告の経路（IADR-0029・IADR-0462）の外に、2 本目の収集の経路ができる。サービスの「到達不能」の判定も 2 つに割れる。

## 結果

- 構成取得 API と SC-11 で、登録済みのデータソースと有効・無効が見える。計画の「配備までの暫定手段」（SC-06 で確かめる）は、配備の後に不要になる。
- 残余:
  - データソースの数に比例して、応答が大きくなる（上限は置かない。PoC は数件）。
  - データソースサービスの DB が落ちると、構成取得 API ではデータソースサービス全体が「到達不能」と出る（種別・ポートも見えない）。IADR-0660 の規律のとおりである。
