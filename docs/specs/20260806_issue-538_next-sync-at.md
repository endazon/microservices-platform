---
title: SC-06「次回同期」— 共通間隔の次回実行時刻を NextSyncAt として返す
type: spec
status: done
related_ids: [SC-06, UC-04, FR-01, FR-02, IADR-0039, IADR-0051, IADR-0074, IADR-0083, IADR-0122, IADR-0131, IADR-0132, IADR-0136]
author: Claude
created: 2026-08-06
updated: 2026-08-06
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ../adr/IADR-0136_next-sync-at-from-worker-cadence.md
  - ../adr/IADR-0051_datasource-connector-port-and-filesystem.md
  - ../adr/IADR-0083_datasource-sync-single-writer-advisory-lock.md
  - ../adr/IADR-0132_openapi-required-from-csharp-nullability.md
  - ../screens/SC-06_datasource-management.md
  - ../tests/SC-06_datasource-management.md
  - ../api/BFF_bff-surface.md
  - ../data/data-source.md
---

# 仕様書: SC-06「次回同期」を共通間隔の次回実行時刻として返す（#538）

> 本仕様書は実装着手前に作成した。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-06 データソース管理画面**
  （[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-06
  「同期健全性・次回同期・更新 API の確定（2026-08-05。利用者裁定〔質問票 第12回 Q14〜Q16〕）」の **Q15**）
- 機能要求（FR）: **FR-01**（データソースの登録・同期・カタログ化）・**FR-02**（取り込み）
- ユースケース（UC）: **UC-04**（基本 2「システムが**定期的に**原本を取得する」）
- 関連 ADR（計画）: ADR-0002（DB per Service）。**ソース別スケジュールに関する計画 ADR は無い**（裁定が本文で確定した）
- 関連 IADR: [[IADR-0136]]（本作業の設計判断）・[[IADR-0051]]（定期同期ワーカー）・[[IADR-0074]]（Helm 配線）・
  [[IADR-0083]]（単一書き手化）・[[IADR-0132]]（`required` は C# の非 null 性から）・[[IADR-0131]]（OpenAPI が BFF 契約の正）
- 環流の経緯: [feedback/20260805_sc05-07-admin-contract-gaps.md](../../feedback/20260805_sc05-07-admin-contract-gaps.md)
  → planning#198 → 裁定 → planning#200（計画本文へ反映済み）

計画の確定文（引用）:

> **「次回同期」列は残すが、ソース別スケジュールは持たない**（Q15）。同期は全ソース**共通の間隔**で回る
> hosted service であり、`NextSyncAt` は**共通間隔の次回実行時刻**として全ソース同じ値を返す。

## 目的・背景

SC-06 の「次回同期」列は、契約に該当する値が無いため**実装しない要素**として繰り延べていた
（[画面仕様書 §実装しない要素 (b)](../screens/SC-06_datasource-management.md)）。裁定により
**ソース別スケジュールはモデル化せず**、共通間隔から答えられる「次に取り込まれるのはいつか」だけを
契約へ載せることが決まった。本作業はその契約（`DataSourceDto.NextSyncAt`）を用意する。

## 着手時の実測（「共通間隔」の実体はどこにあるか）

推測でキー名を作らないため、母集合を数え切ってから設計した。

```console
$ git grep -n "IntervalSeconds" -- src deploy scripts | grep -i datasource
deploy/helm/microservices-platform/templates/deployment.yaml:93:  - name: DataSourceSync__IntervalSeconds
src/.../DataSourceService.Api/Foundation/Services/DataSourceSyncHostedService.cs:29:  var interval = TimeSpan.FromSeconds(Math.Max(30, opt.IntervalSeconds));
#   ↑ 行番号は着手時点。本作業で StartSchedule() へ切り出したため現在は同メソッド内にある。
src/.../DataSourceService.Api/Foundation/Services/DataSourceSyncOptions.cs:13:  public int IntervalSeconds { get; set; } = 300;
$ grep -rn "DataSourceSync" deploy/docker-compose.yml    # → 0 件
```

| # | 層 | 実体 | 値 |
| --- | --- | --- | --- |
| 1 | コード既定 | `DataSourceSyncOptions.IntervalSeconds`（`DataSourceSync` セクション） | **300 秒**。`Enabled` の既定は **false** |
| 2 | 実効値 | `DataSourceSyncHostedService.StartSchedule()` | `Math.Max(30, IntervalSeconds)` 秒（**過負荷防止の 30 秒床**） |
| 3 | 配備（k8s） | `values.yaml` `services.datasource.dataSourceSync`（`enabled: true` / `intervalSeconds: 300`）→ env `DataSourceSync__Enabled` / `DataSourceSync__IntervalSeconds`（[[IADR-0074]]） | **300 秒・有効** |
| 4 | 配備（compose / ローカル） | `deploy/docker-compose.yml` に **記述なし** | 既定のまま＝**定期同期は無効** |

**判明した事実 2 つが設計を決めた。**

1. **間隔は設定に存在する**（推測で新設する必要は無い）。ただし**位相（いつ回るか）はどこにも永続化されていない**。
   `PeriodicTimer` はワーカーの起動時刻を起点に刻むだけで、次回時刻を保持する場所は無い。
2. **既定は無効**であり、compose では有効化されない。つまり「次回同期が**無い**」状態が正当に存在する。

## 対象範囲

### 対象

- `Knowledge.Contracts.Dtos.DataSourceDto` への `NextSyncAt`（`DateTimeOffset?`）追加。
- `DataSourceService` の `/datasources`（一覧・個別・登録）応答へ `nextSyncAt` を載せる。
- BFF `/bff/datasources`（一覧・個別・登録）での透過。
- `docs/api/openapi.yaml` の `DataSourceDto` スキーマへ `nextSyncAt` を追加。
- 契約スナップショット（`scripts/contract-schema-baseline.json`）と orval 生成物の更新。
- 上記のテスト（xUnit）と仕様書（画面・テスト・データ・通信・IADR）の更新。

### 対象外（送り先を明記する）

| 事項 | 理由・送り先 |
| --- | --- |
| **ソース別スケジュール（cron / ソース単位の interval）** | **裁定で不採用**（planning#200 Q15）。要求が具体化した時点で計画側が FR を起こす |
| SC-06 画面への「次回同期」列の追加（表示） | 本 issue は契約（**返す**こと）が範囲。表示は SC-06 の画面作業で扱う。画面仕様書 §hi-fi 対応 #7 の状態を「契約の不在」から「表示は未実装」へ改める |
| 同期健全性（連続失敗回数・再試行上限・直近エラー。裁定 Q14） | 別作業。琥珀の充て先は [[IADR-0127]] 決定 2 のまま空けておく |
| データソース更新 API（裁定 Q16） | 別作業 |
| 次回実行時刻の**永続化**（DB 列） | 導出値であり状態ではない。§設計 決定 4 |

## 設計

### 決定（詳細と却下案は [[IADR-0136]]）

1. **`NextSyncAt` はワーカーの起動時刻を起点とする共通間隔の次回境界**とする。
   `SyncSchedule`（singleton・インメモリ）がワーカー起動時に「起点時刻 ＋ 実効間隔」を記録し、
   読み出し時に `起点 + 間隔 × (経過 ÷ 間隔 + 1)`（＝現在時刻より真に後の最初の境界）を返す。
2. **定期同期が無効なら `null`**（compose・dev・`Enabled=false`）。「次回がある」と偽らない。
3. **全ソース同値**。一覧は `SyncSchedule` を**1 回だけ読み**、その値を全行へ配る
   （行ごとに読むと境界を跨いだ瞬間に列内で値が割れる）。
4. **永続化しない**。次回時刻はワーカーの位相から導出できる値であり、DB へ持つと再起動のたびに嘘になる。
5. **型は `DateTimeOffset?`（UTC）**。既存の `LastSyncedAt` / `CreatedAt` と同じ作法に揃える
   （`Knowledge.Contracts` の日時メンバーは実測 **15 件すべて `DateTimeOffset`**・素の `DateTime` は **0 件**。
   `grep -rhoE "DateTimeOffset\??\s+[A-Za-z]+" src/knowledge/backend/Shared/Knowledge.Contracts/ | wc -l`）。
   タイムゾーン変換は表示側の責務であり、契約は UTC のオフセット付きで返す。
6. OpenAPI では **`nullable: true` かつ `required` に入れない**（[[IADR-0132]] 論点 A の A1: `required` は
   C# の非 null 性から起こす。`DateTimeOffset?` は null で来る）。

### なぜ「現在時刻 ＋ 間隔」ではないか（却下案の要点）

`now + interval` は要求のたびに値が動き、**「次回同期」が永遠に来ない**表示になる。
`LastSyncedAt + interval` はソース別の値になり（＝裁定違反）、かつ**同期失敗時は `LastSyncedAt` が
前進しない**（[[IADR-0051]] 決定 3a）ため過去の時刻を「次回」として出す。
固定エポックからの切り上げは、実際に回るワーカーの位相と無関係な数になる。

### 変更点

| # | ファイル | 変更 |
| --- | --- | --- |
| 1 | `Knowledge.Contracts/Dtos/DataSourceDto.cs` | 末尾へ `DateTimeOffset? NextSyncAt = null`（**既定値つき＝非破壊**。[[IADR-0122]] 判定） |
| 2 | `DataSourceService.Api/Foundation/Services/SyncSchedule.cs`（新規） | 共通間隔の位相を保持し次回境界を返す singleton |
| 3 | `.../Services/DataSourceSyncHostedService.cs` | 起動時に `SyncSchedule.Start(interval)`。起動処理を `internal StartSchedule()` へ切り出し（テスト可能化） |
| 4 | `.../Endpoints/DataSourceEndpoints.cs` | `ToResponse(ds, nextSyncAt)`。一覧は 1 回読んだ値を全行へ配る |
| 5 | `.../Program.cs` | `TimeProvider.System` と `SyncSchedule` を DI 登録 |
| 6 | `docs/api/openapi.yaml` | `DataSourceDto.nextSyncAt`（`date-time` / `nullable`） |
| 7 | `scripts/contract-schema-baseline.json` | `--update` で更新（差分がレビュー対象） |
| 8 | `platform/frontend/src/foundation/api/generated/bff.schemas.ts` | `pnpm run codegen` の再生成物（コミット対象） |

BFF（`DataSourceBffEndpoints`）は `DataSourceDto` で型付けして中継するだけなので**コード変更は不要**である
（DTO にメンバーが増えれば往復する）。この「変更不要」がテストで固定されていないと、
将来の書き換えで静かに落ちるため BFF 側にも通過テストを 1 本置く。

## 受け入れ基準

- [x] `GET /bff/datasources` の各要素が `nextSyncAt` を持つ（定期同期が有効なとき）。**実装済み・テスト B1 / E1**
- [x] `nextSyncAt` は**全ソースで同一値**である（ソース別スケジュールを持たない裁定の写し）。**テスト E1**
- [x] 値は**共通間隔の次回実行時刻**であり、現在時刻より後である。**テスト S2 / S3 / S4 / W2 / W3**
- [x] 定期同期が無効なとき `nextSyncAt` は `null`（「次回がある」と偽らない）。**テスト S1 / W1 / E2**
- [x] 契約（OpenAPI・`contract-schema-baseline.json`・orval 生成物）が実装と一致している。**実行して確認済み**
- [x] ソース別のスケジュール設定（cron / ソース単位 interval）を**足していない**。**差分で確認**

> 上の 1〜4 は受け入れ基準をテストへ写像したものであり、**SDK コンテナで実際に走らせて全件合格を
> 確認した**（`DataSourceService.Api.Tests` 78 件・`Platform.Bff.Tests` 149 件 ＋ skip 1）。
> 5・6 は本環境のスクリプト検査で確認した（§検証）。

## テスト方針

時刻依存を決定的にするため、`TimeProvider` を注入する（`DateTimeOffset.UtcNow` をテストから呼ばない）。
既存の時刻抽象の実測: 本リポジトリは **BCL の `TimeProvider`** を使う
（`Platform.Shared.Infrastructure/.../ConfigInspectionService.cs` ほか。`IClock` 等の自前抽象は 0 件、
`FakeTimeProvider` パッケージも不採用）。`SyncScheduleTests` に 5 行の `FixedTimeProvider` を置く
（新規パッケージは足さない）。BFF 側は後段をスタブする層なので、固定値 `BffTestFactory.StubNextSyncAt` を用いる。

| # | 観点 | 起点 | テスト |
| --- | --- | --- | --- |
| S1 | 未起動なら次回は無い | 裁定 Q15 / [[IADR-0136]] 決定 2 | `NextRunAt_WhenNeverStarted_IsNull` |
| S2 | 起動直後は「起点 ＋ 間隔」 | UC-04 基本 2 | `NextRunAt_JustAfterStart_IsOneIntervalAhead` |
| S3 | 何周期か経過しても**現在より後の境界** | 同上 | `NextRunAt_AfterSeveralIntervals_IsNextBoundary`（Theory） |
| S4 | 境界ちょうどでは次の境界 | 同上 | `NextRunAt_ExactlyOnBoundary_MovesToNextBoundary` |
| W1 | 無効なら位相を記録しない | 実測 4（compose は無効） | `StartSchedule_WhenDisabled_LeavesScheduleUnset` |
| W2 | 有効なら起動時刻を起点に記録する | [[IADR-0051]] | `StartSchedule_WhenEnabled_AnchorsAtStartup` |
| W3 | 30 秒床が次回時刻にも効く | ワーカー `StartSchedule()` | `StartSchedule_FloorsIntervalAtThirtySeconds`（Theory） |
| E1 | 一覧の全ソースが同値 | **裁定 Q15 の核心** | `ListDataSources_ReturnsSameNextSyncAtForEverySource` |
| E2 | 無効時は null | [[IADR-0136]] 決定 2 | `ListDataSources_WhenPeriodicSyncDisabled_ReturnsNullNextSyncAt` |
| B1 | BFF が透過する | [[IADR-0039]] | `GetList_PassesThroughNextSyncAt` |

## 検証（実測）

実行日 2026-08-06・ブランチ `feat/SC-06-next-sync-at`。

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-contract-schema.js` | **OK**（2 プロジェクト / 20 ファイル / 57 型が baseline と一致・未消化の承認 0 件）。`--update` の差分は本 PR に含む |
| `node scripts/check-doc-links.js` | **OK**（440 件の Markdown に破損した相対リンクなし。`src/ai-stock-trading` 配下 2 件は未 populate のため対象外） |
| `node scripts/check-test-spec-coverage.js` | **OK**（`--update` 後。仕様書 × クラスの対 69 件が床と一致） |
| `node scripts/check-test-traceability.js` | **OK**（仕様書のある起点 ID 28 件中 28 件が写像済み） |
| `node scripts/check-bff-downstreams.js` / `check-unit-dependencies` / `check-backend-libraries` / `check-cpm-versions` / `check-i18n-catalogs` / `check-unit-service-ownership` | **すべて exit=0** |
| `node --test scripts/scripts.test.js` / `scripts.repo.test.js` | **OK**（fail 0） |
| `pnpm run codegen`（`src/`） | 生成物 `bff.schemas.ts` に `nextSyncAt?: string \| null` が入り、faker も追随。**再生成しても差分が出ない**状態でコミットした |
| `pnpm run lint`（`src/`） | **OK**（0 errors / 8 warnings。warning はすべて既存の `react-refresh/only-export-components`） |
| `npx vitest run knowledge/frontend/src/features/sc06-datasources` | **OK**（**23 passed** / 2 files。画面は未変更） |
| `pnpm run typecheck`（`src/`） | `src/ai-stock-trading` を populate すれば通る（下記） |
| `dotnet build src/knowledge/backend/backend.slnx` | **green**（0 Error） |
| `dotnet build src/platform/backend/backend.slnx` | **green**（0 Error）。**`src/ai-stock-trading` の populate が要る**（下記） |
| `dotnet format <slnx> --verify-no-changes`（両ユニット） | **差分なし** |
| `dotnet test …/DataSourceService.Api.Tests` | **78 件すべて合格** |
| `dotnet test …/Platform.Bff.Tests` | **149 件合格 / 1 件 skip** |

#### `pnpm run typecheck` の失敗は環境由来である（本作業の変更ではない）

```console
knowledge/frontend typecheck: Done
platform/frontend typecheck: src/features/index.ts(14,52): error TS2307:
  Cannot find module '@ai-stock-trading/features' or its corresponding type declarations.
$ git submodule status | grep ai-stock-trading
-655e2ed1aa2bde8ed35ffe353c3bc88eb10796f0 src/ai-stock-trading   # 先頭 '-' = 未 populate
```

**`src/ai-stock-trading`（別プロジェクトの submodule）が未 populate であることが原因**で、
本作業が触っていない `platform/frontend/src/features/index.ts` が落ちる。同じ理由で
`pnpm run test`（全体）は **6 件失敗**する（`initialChunk.test.ts` / `router.test.ts` / `Layout.test.tsx`。
いずれも `Failed to resolve import "@ai-stock-trading/features"`）。**本作業が触る SC-06 の 23 ケースと
`knowledge/frontend` の typecheck は通っている。** submodule を populate できる環境（CI）では解消する。

### バックエンドの検証手順（`dotnet` はホストに無いが SDK コンテナで実走できる）

当初この節は「本作業環境に .NET SDK が無く、導入も出口ポリシーで塞がれている」として
`dotnet build` / `test` / `format` を未検証としていた。**それは `dotnet-install.sh` でホストへ入れる
経路だけを試した結果である**（`builds.dotnet.microsoft.com:443` はプロキシに 403 で拒否される）。
**docker は使えるので、SDK コンテナで実走できる。**

```console
$ docker run --rm --network host \
    -v "<worktree>:/w" \
    -v /root/.ccr/ca-bundle.crt:/usr/local/share/ca-certificates/ccr.crt:ro \
    -v "<scratchpad>/nuget:/root/.nuget/packages" \
    -w /w -e HTTPS_PROXY=http://127.0.0.1:39793 -e HTTP_PROXY=http://127.0.0.1:39793 \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    bash -lc 'update-ca-certificates >/dev/null; dotnet build src/knowledge/backend/backend.slnx'
```

**素の `docker run` では NuGet が全滅する**（`NU1301 … The remote certificate is invalid …
UntrustedRoot`。122 Error）。必要なのは次の 2 点である。

- **`--network host`**。プロキシは**ホストの** `127.0.0.1:39793` にあり、コンテナ内の `127.0.0.1` は別物。
- **CA をコンテナの信頼ストアへ入れる**。`SSL_CERT_FILE` を渡すだけでは NuGet の SSL 検証を通らない。
  `/usr/local/share/ca-certificates/` へ置いて `update-ca-certificates` を走らせる。

**`src/platform/backend` のビルドには `src/ai-stock-trading` の populate が要る。** 未 populate だと
`BffEndpointComposition.cs(1,7): error CS0246: … 'AiStockTrading' could not be found` で落ちる
（**本作業とは無関係**。`git submodule update --init -- src/ai-stock-trading` で解消し、pin は動かない）。
同じ populate で `pnpm run typecheck` の `@ai-stock-trading/features` 解決失敗も解消する。

### 変異試験

「壊すと落ちる」ことを実測する。C# 側の変異（M4 / M6）も上記のコンテナ経路で**実際に当てた**。
**素通りするもの（M5）は隠さず開示する。**

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M1 | `openapi.yaml` の `DataSourceDto.nextSyncAt` を削除 | 生成物に差分が出る（CI の `codegen && git diff --exit-code` が落ちる） | **落ちた**。再生成した `bff.schemas.ts` から `nextSyncAt?: string \| null` を含む **5 行が消え**、正しい生成物との `diff` は exit=1 |
| M2 | C# `DataSourceDto` から `NextSyncAt` を削除 | 契約スナップショットが不一致 | **落ちた**。`check-contract-schema.js` exit=1 —— `[破壊的] メンバーの削除: Knowledge.Contracts.Dtos.DataSourceDto.NextSyncAt` |
| M3 | `NextSyncAt` の既定値 `= null` を外す | 破壊的変更として fail（旧発行者の位置引数が壊れる） | **落ちた**。exit=1 —— `[破壊的] メンバーの必須化: …DataSourceDto.NextSyncAt（省略可能 → 必須）` |
| M4 | `SyncSchedule.NextRunAt` を「起点 ＋ 間隔」固定へ戻す（何間隔経過しても 1 回分しか進まない） | S3 / S4 が落ちる | **落ちた**。`Failed: 3 / Passed: 75` —— `NextRunAt_AfterSeveralIntervals_IsNextBoundary`（`elapsed 7 分 → 10 分` / `23 分 → 25 分`）と `NextRunAt_ExactlyOnBoundary_MovesToNextBoundary` |
| M5 | 一覧で行ごとに `NextRunAt` を読む | E1 は**落ちない見込み**（固定時計では境界跨ぎを再現できず同値になる） | **未実測**。**素通りが見込まれる変異である**（下記） |
| M6 | `StartSchedule` の `Math.Max(30, …)` を外す | W3 が落ちる | **落ちた**。`Failed: 1 / Passed: 77` —— `StartSchedule_FloorsIntervalAtThirtySeconds(configured: 5, effectiveSeconds: 30)` |

M4 / M6 とも変異を戻して **78 件合格へ復帰**することを確認した（変異が残っていないこと・テストが
変異そのものに反応したことの両方を示す）。

**M5 は素通りする見込みであることを隠さずに書く。** 「一覧では 1 回だけ読む」という規則を機械で守らせるには、
エンドポイント越しに境界を跨ぐ時計を動かす（＝応答生成の途中で時刻を進める）必要があり、本作業の射程を超える。
規則は §設計 決定 3・コード注釈・[[IADR-0136]] §限界 の 3 箇所に残した。

**M1〜M3 の実行後は元に戻し、`check-contract-schema.js` が OK・生成物が再生成と一致することを再確認した。**

## 計画書との差異

- 差異: **なし**。裁定（planning#200 Q15）の範囲どおりに実装し、ソース別スケジュールは足していない。
- 計画本文は「`NextSyncAt` は共通間隔の次回実行時刻として全ソース同じ値を返す」とだけ定め、
  **定期同期が無効なときの値を定めていない**。実装は `null`（＝次回は無い）とした。
  計画の意図（「次に取り込まれるのはいつか」に答える）に反しないため環流はしないが、
  画面が「—」等をどう出すかは表示側の作業で決める。

## 未決事項・親への申し送り

1. **バックエンドの検証手順を残した**（§検証）。`dotnet` はホストに無いが SDK コンテナで実走できる。
   **`--network host` とプロキシ CA の投入が要る**（素の `docker run` は NuGet が `NU1301 UntrustedRoot`
   で全滅する）。この手順は仕様書にしか書かれておらず、**次に C# を触る作業者が同じ壁で
   「SDK が無い」と結論しかねない**。`docs/how-to/` へ切り出す価値がある。**別 issue の候補。**
2. **表示（SC-06 の「次回同期」列）は未実装**。契約が揃ったので繰り延べの理由は消えた。
   画面作業で拾うこと（画面仕様書 §未決事項 2 を書き換え済み）。
3. **マルチレプリカでの位相差**（[[IADR-0136]] §限界）。応答するレプリカ自身の位相を返すため、
   実際に同期を行うレプリカ（advisory lock を取った側・[[IADR-0083]]）とは最大 1 間隔ずれ得る。
   運用者への意味（「次に取り込まれるのはいつ頃か」）は保たれるため許容する。
4. **作業環境の制約は 2 件とも回避できた**（本作業に固有の欠陥ではない）。①`dotnet` はホストに無いが
   **SDK コンテナで実走できる**（§検証）、②`src/ai-stock-trading` submodule は
   `git submodule update --init -- src/ai-stock-trading` で populate でき、これで
   `platform/backend` のビルドと `platform/frontend` の typecheck / 一部テストの失敗が解消する
   （**pin は動かない**ので実装ブランチで行ってよい）。**どちらも本作業の変更とは無関係**であった。
