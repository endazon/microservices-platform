---
title: 作業仕様書 — 構成取得 API と SC-11 に、SC-06 に登録したデータソースと有効・無効を載せる（#2285 の (b)(ii)）
type: spec
status: done
related_ids: [FR-15, FR-01, SC-06, SC-11, ADR-0018, IADR-0029, IADR-0660, IADR-0666]
author: claude
created: 2026-10-31
updated: 2026-10-31
issue: "#2285"
---

# 作業仕様書 — 登録済みのデータソースを構成取得 API へ（#2285 の (b)(ii)）

> 本仕様書は、実装に着手する前に作成した（着手 2026-10-31。develop `49dd9844`）。
> 範囲は #2285 の残り (b)(ii) だけである。(d-2)（MCP の実効ツール）は #2368 でマージ済み。

## 計画の裁定（planning#850・利用者裁定 2026-10-30）

計画 `06_technical/10_composability-design.md` §6 の応答項目の表は、次のように書き分けられた。

| 計画の行 | 中身 |
| --- | --- |
| コネクタ一覧 | **SC-06 に登録したデータソースと有効・無効**（09_datasource-connectors の管理情報） |
| コネクタの種別（注記） | 登録済みのコネクタのプラグイン 4 種。常に有効。「コネクタ一覧」とは**別の項目**である |

- 現在の実現手段（計画の記述）: 種別は実装済み（MSP#2286）である。データソースは、構成取得 API にまだ無い。
- 配備までの暫定手段（計画の記述）: SC-06 で確かめる。

## 現状（実測。develop `49dd9844`）

- 自己申告の `Connectors`（proto `connectors = 4`）は、データソースサービスが起動時に申告する**プラグインの種別 4 種**である（`Program.cs` の `AddConnector(... enabled: true)`）。
- 実効構成 `EffectiveConfigDto.Connectors` は、サービス横断で名前で一意化した、同じ種別の一覧である。
- SC-11 は、それを「コネクタ」の表（名前・状態）で出す。
- 要求のたびの寄与（`IIntrospectionContributor`。IADR-0660）がある。寄与の器は `Connectors` と `McpTools` の 2 つである。
  - IADR-0660 は「(b)(ii) は DataSourceService がコネクタを寄与する形で足せる」と書いた。
- データソースは `DataSources` 表にある（`Id`・`Name`・`SourceType`・`Status`〔`active` / `disabled`〕）。

## 方針（IADR-0666）

1. **別の項目として足す。`connectors` に混ぜない。**
   - 計画が「コネクタ一覧」と「コネクタの種別」を別の項目にしたためである。
   - 既存の `connectors` の意味を変えると、破壊的な変更になる。
   - 名前で一意化している既存の集約に入れると、同名のデータソースが 1 件に潰れる。
   - 契約の対応は次のとおりである。
     - 新しい `dataSources`（`DataSourceSummaryDto`: `id`・`name`・`type`・`enabled`）が、計画の「コネクタ一覧」に当たる。
     - 既存の `connectors` が、計画の「コネクタの種別」に当たる。
   - 契約への非破壊の追加として入れる。
     - proto: `repeated DataSource data_sources = 6`。
     - DTO: 末尾に既定値つきで足す。
     - openapi: `required` に足す（応答は常に配列）。
2. **寄与の器を足す。** `IntrospectionContribution` に `DataSources` を足す（末尾・既定値つき）。`ApplyAsync` は静的な申告に足す（静的な申告に、データソースは無い）。
3. **データソースサービスの寄与**: `RegisteredDataSourcesIntrospectionContributor`。
   - 要求のたびに、スコープを作って DB を読む。
   - 状態に依らず全行を読み、名前→ID の順に並べる。
   - `enabled` は `Status == active` のときに真とする。
   - 出すのは ID・名前・種別・有効だけである。接続先・設定・資格情報は出さない。
   - DB の例外は握り潰さない（IADR-0660 のとおり）。gRPC の失敗になり、BFF はデータソースサービスを「到達不能」にする。空の一覧を「データソースが無い」と読ませないためである。
4. **集約**（`ConfigInspectionService.Assemble`）: サービス横断で結合し、ID で一意化して、名前→ID の順に並べる。ドリフト検出には使わない（実行時データであり、Git の宣言に無い）。
5. **SC-11**:
   - 実効構成の節に「データソース（コネクタ一覧）」の表を足す（名前・種別・状態）。0 件は空の状態で出す。
   - 既存の「コネクタ」の表は「コネクタの種別」と表記を改める。説明に「プラグインは常に有効」と書く。

## 受け入れ基準（issue の案の 3 つ目 ＋ 寄与の再確認）

- [x] 実効構成 API の応答のデータソースの一覧（`dataSources`）に、登録済みのデータソースとその有効・無効が出る。
- [x] 登録・無効化の後の、次の要求に反映される（データソースサービスの寄与の試験。DB の実行時データ）。
- [x] 既存の `connectors`（種別 4 種）は変わらない。
- [x] 寄与が例外を投げたとき、gRPC の失敗になる（握り潰さない。既存の試験の形）。
- [x] SC-11 に「データソース（コネクタ一覧）」の表が出る。0 件で空の状態が出る。種別の表の表記が改まる。
- [x] 出力に接続先・設定・資格情報が含まれない（DTO の形で固定する）。

## 母集合（規則 9・10）

- **規則 9**: 走査の語は「`connectors`」「コネクタ一覧」「狭い読み」「広い読み」「(b)(ii)」「planning#850」、コンストラクタは「`EffectiveConfigDto(`」「`ServiceIntrospectionDto(`」「`IntrospectionContribution(`」である。
  - 契約: `ConfigInfoDto.cs`、`service_introspection.proto`、`IntrospectionGrpcService.cs` の写し、`docs/api/openapi.yaml`、orval の生成物、`scripts/contract-schema-baseline.json`、`scripts/proto-contract-baseline.json`。
  - 偽物の応答（`connectors:` で引いた 5 本）: `opsFlow.test.tsx`、`ConfigViewerPage.test.tsx`、`sc11-config/routes/access.test.tsx`、`e2e/a11y.smoke.spec.ts`、`e2e/sc11-config.smoke.spec.ts`。`dataSources` を足す。
  - 文書:
    - `docs/functional/FR-15_config-info-api.md`: 113〜120 行の「狭い読み・答え待ち」を改める。
    - `docs/screens/SC-11_configuration-viewer.md`: 231 行の表。
    - `docs/tests/FR-15_config-info-api.md`: 2k の行。データソースの行を足す。
    - `docs/tests/SC-11_configuration-viewer.md`。
  - 凍結記録（書き換えず、日付つきで追記する）:
    - IADR-0029 フォローアップ 2（狭い読み・答え待ち）。
    - IADR-0660 決定 1（「コネクタを寄与する形」と、(b)(ii) は未着手）。
  - コードのコメント（答え待ちと書いた箇所）:
    - DataSourceService `Program.cs` の 90〜95 行。
    - `IntrospectionContributor.cs` の 9〜11・18 行。
    - `IntrospectionGrpcService.cs` の 20 行。
    - `IntrospectionEndpointTests.cs` の 36 行。
  - 対象外: `docs/api/east-west-grpc.md:270` と `docs/security/security.md:325` の「狭い読み」は、別の主題（利用者名簿の読み口）である。
- **規則 10**（この変更で新たに誤りになる自分の記述）: SC-11 の「コネクタ」の空の状態の文言（「コネクタは Git（GitOps）の宣言で定義します」）は、種別には当たるが、データソースには当たらない。データソースの表には「SC-06 で登録する」と書く。

## 残余リスク

- データソースの数に比例して、応答が大きくなる（PoC は数件の規模）。上限は置かない。
- データソースサービスの DB が落ちると、構成取得 API ではデータソースサービス全体（種別・ポートを含む）が「到達不能」になる（IADR-0660 の規律）。

## 検証（2026-10-31・コミット `606a7c55`）

| 対象 | 結果 |
| --- | --- |
| `dotnet test` platform | 合格 |
| `dotnet test` knowledge | 合格。ただし `Knowledge.IntegrationTests` の `WikiSlugNonUniquePostgresTests` 1 件が失敗した。実 PostgreSQL の接続文字列が無いためで、本変更とは関係しない |
| `dotnet format --verify-no-changes`（両ユニット） | 差分なし |
| `pnpm run test` | 2156 件合格 |
| `pnpm run typecheck`・`lint`（警告は既存のもの）・`format:check` | 合格 |
| `pnpm run build` ＋ `check-chunk-budget --require` | 合格。床は 658,882 B に上げ、理由は `$comment` に書いた |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 1226 件合格 |
| 文書・契約の検査 | すべて OK。対象は trace-blocks・adr-numbering・cross-repo-refs・plan-id-qualification・nul-bytes・doc-updated・doc-type-vocabulary・contract-schema・proto-contracts・test-spec-coverage・i18n-catalogs・doc-links・knowledge-graph |
