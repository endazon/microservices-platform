---
title: データソース契約の束 — 更新 API（#534）と同期健全性（#537）
type: spec
status: draft
related_ids:
  - FR-01
  - FR-02
  - SC-06
  - UC-04
  - IADR-0039
  - IADR-0051
  - IADR-0116
  - IADR-0122
  - IADR-0127
  - IADR-0136
  - IADR-0139
  - IADR-0141
  - IADR-0148
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - "../screens/SC-06_datasource-management.md"
  - "../functional/FR-01_data-source-catalog.md"
  - "../data/data-source.md"
  - "../tests/SC-06_datasource-management.md"
  - "../api/BFF_bff-surface.md"
  - "../adr/IADR-0148_datasource-sync-health-persistence.md"
  - "../adr/IADR-0139_domain-bundled-contract-prs.md"
  - "../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md"
---

# 仕様書: データソース契約の束（#534 ＋ #537）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を
> 一次情報とし、本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

本作業は **[[IADR-0139]] 決定 5 が「束ねる」と判定した 2 件**（#534 ＋ #537）を 1 PR で実装する。
束ねる根拠と条件の当て直しは §束の判定 に置く。**issue ごとの節**（§#534 / §#537）を必ず読むこと
（[[IADR-0139]] 決定 6-3）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-01**（データソースカタログ）・**FR-02**（取り込み）
- ユースケース（UC）: **UC-04**（データソース同期。**例外フロー「継続失敗はアラートする」**）
- 画面（SC）: **SC-06**（データソース管理画面）
- 関連 ADR:
  - 計画: `ADR-0004`（存在秘匿・404 原則。本作業では認可の維持のみ）
  - 実装: [[IADR-0039]]（BFF とロールゲート）／[[IADR-0051]]（コネクタポートと同期オーケストレーション）／
    [[IADR-0053]]（応答での秘密マスク）／[[IADR-0122]]（契約スキーマの後方互換ゲート）／
    [[IADR-0127]] 決定 2（**契約から導出できる値だけを表示する**。琥珀の予約）／
    [[IADR-0136]]（`NextSyncAt` は共通間隔）／**[[IADR-0148]]（本作業で新設）**
- 計画書リンク:
  [`05_screens/01_screens.md` §SC-06](../../planning/projects/microservices-platform/05_screens/01_screens.md)
  （2026-08-05 の確定「同期健全性・次回同期・更新 API」。利用者裁定〔質問票 第12回 Q14〜Q16〕・planning#198）

## 束の判定（[[IADR-0139]] 決定 1 の 6 条件を着手直前に当て直した）

[[IADR-0139]] 決定 5 の注記は「実効表は 2026-08-07 時点のスナップショットであり、**束を組む直前に
条件 A〜F を当て直すこと**」と定める。当て直した結果は次のとおりで、**判定は変わらない**。

| 条件 | 判定 | 実測の根拠 |
| --- | --- | --- |
| **A. 同一資源** | ✅ | 両 issue とも資源は `/datasources`（＋ BFF の `/bff/datasources`）ただ 1 つ、DTO は `DataSourceDto` ただ 1 つである。**SC-06 の共有ではなく資源の一致で判定した** |
| **B. 裁定が済んでいる** | ✅ | 両 issue の本文に未解決の質問が無い。**#537 の「契約とアラート発報の境界」は着手前に確定した** —— §#537 の「境界の確定」を参照 |
| **C. 非破壊側に収まる** | ✅ | `DataSourceDto` への追加はすべて**既定値つきの省略可能パラメータ**（[[IADR-0122]] 決定 2 の非破壊側）。既存の位置パラメータの順序・型は変えない。新エンドポイントは追加のみ |
| **D. 1 コミット = 1 issue** | ⚠️ **満たせなかった**（下記） | 着手時は「#537 → #534 の 2 コミット」を予定したが、**実装後に差分を実測したら分割できなかった**。理由と代替の担保は §条件 D を満たせなかった理由 |
| **E. 着手済みを含まない** | ✅ | 両 issue ともブランチ・PR が存在しない（`git ls-remote --heads origin` と open PR 一覧で実測。open は #605 / #625 のみ） |
| **F. 契約の追加に閉じる** | ✅（判断を記録する） | 再索引・取り込み側（インデクサ / ワーカー）の変更・外部システムの配備を**伴わない**。**EF の追加マイグレーション 1 本を伴う**が、これは**既存データの移行ではない**（すべて null 許容または既定値 0 の列追加で、バックフィルを行わない）。[[IADR-0139]] 条件 F が明示的に除外するのは「既存データの**移行**・**再索引**」であり、**非破壊な列追加はエンドポイントの追加と同じく「契約の追加」の側にある**と判断した。根拠と代替案の棄却理由は [[IADR-0148]] に置く |

**上限**（名目 3 件・実効 2 件）を満たす。本束は 2 件である。

### 条件 D を満たせなかった理由（**予定と実測が食い違ったので、実測の側を書く**）

**着手時の予定は「#537 → #534 の 2 コミット」だった。実装後に `git diff -U0` でハンクを実測したところ、
分割できないことが分かった。**

| ファイル | 実測 |
| --- | --- |
| `Foundation/Domain/DataSource.cs` | #537 の `RecordSyncFailure` / `ClearSyncFailures` と #534 の `Update` / `Patch` が**連続した 1 ハンク**として入る（`-U0` でも割れない。連続した挿入は 1 ハンクになる） |
| `Shared/Knowledge.Contracts/Dtos/DataSourceDto.cs` | ハンク単位では割れるが、**割ると片方のコミットで契約と生成物が食い違う** |
| orval 生成物・Lingui コンパイル済カタログ・各 baseline（契約 / チャンク / テスト仕様被覆） | **1 回の再生成の産物**であり、issue 別に生成し直すと「自分の入力と一致しない生成物を持つコミット」ができる |

**分割するには実装の側を歪める必要がある**（エンティティのメソッドを issue 順に並べ替える・生成物を
2 回に分けて中間状態を作る）。**規約を満たすために実装を歪めるのは本末転倒**なので、
**1 コミットにまとめ、その事実をここと PR 本文に明記する**方を選んだ。

**代替の担保**: [[IADR-0139]] 決定 3 は「本リポジトリはスカッシュのみで**コミット境界は develop に
残らない**——「1 コミット = 1 issue」だけではトレーサビリティが担保できないことを実測で確かめた」と
述べ、**`Closes #NNN` を issue ごとに 1 行**書くことを担保としている。本 PR はそれを満たす
（`Closes #534` / `Closes #537`）。**着地件名には両 ID を併記する**（スコープから ID を落とさない）。

> **条件 D の見直しを提案する**（実装側では決められないので記録に留める）。
> 条件 A（同一資源）を満たす束ほど**同じファイルの隣り合う場所を触る**ため、条件 D と衝突しやすい。
> **条件 A を満たすことが条件 D を満たしにくくする**という構造がある。**同型 1 回目なので記録に留め、
> 規約の改定は提案しない**（`CLAUDE.md`「検査器・規約の追加は同型の事故が 2 回起きたら」）。

## 母集合（[[IADR-0141]] 決定 1・走査基準 `ae66549`）

**是正・追随の対象範囲は着手時に自分で引く。** 引き方と除外理由を以下に記録する。

**引き方**: `git ls-files` の**全追跡ファイル**へ `grep -l` を当てた（**拡張子で絞らず・行フィルタを継がず**、
パスの除外だけで取る。規則 3・規則 4）。軸は 2 本立てた（規則 5）。

```
git ls-files | grep -v '^src/ai-stock-trading' | xargs grep -ln "DataSourceDto"   → 28 件
git ls-files | grep -v '^src/ai-stock-trading' | xargs grep -ln "datasources"     → 71 件
```

**2 軸の和集合から、本作業で追随させる対象**（＝ 契約・実装・live な権威文書・生成物）:

| 区分 | ファイル |
| --- | --- |
| 契約 | `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/DataSourceDto.cs` |
| サービス | `.../DataSourceService.Api/Foundation/{Domain/DataSource.cs, Endpoints/DataSourceEndpoints.cs, Persistence/DataSourceDbContext.cs, Services/{DataSourceSyncService.cs, SyncFailureTracker.cs}}` ＋ Migrations |
| BFF | `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DataSourceBffEndpoints.cs` |
| フロント | `src/knowledge/frontend/src/features/sc06-datasources/{syncState.ts, DataSourceManagementPage.tsx}` |
| 生成物 | `docs/api/openapi.yaml`・`src/platform/frontend/src/foundation/api/generated/**`・Lingui カタログ（ja / en）・`scripts/contract-schema-baseline.json` |
| live な文書 | `docs/screens/SC-06_*.md`・`docs/functional/FR-01_*.md`・`docs/data/data-source.md`・`docs/tests/{SC-06_*.md, FR-01_*.md}`・`docs/api/BFF_bff-surface.md`・`docs/adr/IADR-0127_*.md`（琥珀の予約が解けたことの追記） |

**除外したものと理由**（規則 6。「黙って除外した」ことでも事故は起きる）:

| 除外 | 理由 |
| --- | --- |
| `docs/specs/` の既存 15 本・`feedback/` 2 本 | **書いた時点の記録**であり、後から注記を足すのは記録の改竄にあたる（`.claude/rules/traceability.md`「母集合」節）。**本仕様書は作業中の PR 自身のものなので対象外にしない** |
| `CHANGELOG.md` | 生成物。`gen-changelog.js` が履歴から作り直す |
| `deploy/grafana/**`・`deploy/local/observability/**`・`perf/k6/README.md` | **Grafana の datasource**（同綴りの別概念）であり、本作業の資源ではない。`grep` の 2 軸目が拾った偽陽性である |
| `docs/superpowers/plans/` | 保管された旧計画（同上の除外規定） |
| `src/ai-stock-trading/**` | 別プロジェクトの submodule。**変更しない**（`session-handoff.md` §3） |
| `docs/adr/` の IADR-0017 / 0044 / 0051 / 0053 / 0063 / 0074 / 0083 / 0089 / 0136 / 0139 | 本作業で**決定が動かない**（参照されるだけ）。IADR-0127 のみ、決定 2 が予約した琥珀の充て先が確定するため追記する |

## 目的・背景

計画は 2026-08-05 に SC-06 について 3 点を確定した（Q14〜Q16）。本作業はそのうち **Q14（同期健全性）と
Q16（更新 API）**を実装する（Q15〔次回同期〕は #538 で着地済み）。

- **#537 / Q14**: `Status` は `active` / `disabled` の 2 値しか無く、**健全性を表現できない**。
  UC-04 例外フローの「継続失敗はアラートする」は**既に確定した要求**であるのに、契約に健全性が無いため
  画面もアラートも要求を満たせない。計画は「データソースの同期は**静かに壊れる**種類の機能であり、
  気づく手段が本画面の状態表示である」と述べている。
- **#534 / Q16**: 更新（PUT / PATCH）が無く、**登録済みソースの変更が「削除→再登録」でしかできない**。
  接続先の変更・認証情報のローテーション・同期対象パスの追加は日常的な運用であり、
  **削除→再登録は ID と履歴を切る**（文書の出所の追跡が切れるのは監査上受け入れがたい）。

## 対象範囲

- **対象**: `DataSourceDto` への健全性 3 項目の追加／`DataSource` エンティティへの健全性の永続化と
  追加マイグレーション／継続失敗しきい値の**再試行上限への統一**／`PUT` `PATCH` の新設
  （DataSourceService ＋ BFF）／SC-06 の琥珀表示の実装（[[IADR-0127]] 決定 2 の予約の解除）／
  OpenAPI・orval 生成物・Lingui カタログ・契約ベースラインの追随／テストと仕様書の追随。
- **対象外**:
  - **`POST` / `DELETE` の認可を管理者限定へ狭めること**。計画 §SC-06 は「登録・更新・無効化は管理者限定」と
    定めるが、現行実装は 3 つとも `admin` ＋ `operator` である。**これは本束より前から在る差異**であり、
    本作業で一緒に直すと束の射程を越える（[[IADR-0116]] 規約 4「PR ではなく issue を分割する」）。
    **新設する `PUT` / `PATCH` は計画どおり管理者限定で作る**（新しい口を計画に反していない状態で生む）。
    既存 2 口の差異は**別 issue として起票する**（§計画書との差異）。
  - 健全性のプロセス横断集約（複数インスタンス間の合算）。**単一書き手**は
    [[IADR-0083]] のアドバイザリロックで担保されており、本作業は永続化までで足りる（[[IADR-0148]]）。
  - Alertmanager 等への発報経路の配備（**発報は既に構造化ログで実装済み**。§#537「境界の確定」）。

## 設計

### #537: 同期健全性（Q14）

#### 境界の確定 —— **[[IADR-0139]] 条件 B の前提**

[[IADR-0139]] 決定 5 は「**契約（`DataSourceDto` の健全性項目）とアラート発報の境界を着手前に
確定できなければ条件 B を満たさない**」と条件付けている。**実測して確定した。**

**アラート発報は既に実装されている。** `DataSourceSyncService.AlertOnFailure`（`:110-121`）が
連続失敗を記録し、しきい値以上で**構造化ログ（`Alert=true`）**を出す。これは [[IADR-0051]] 決定 3a が
定めた「オーケストレータが watermark 非前進・継続失敗アラートに載せる」の実体であり、
4 つのコネクタのコメントが揃ってこれを参照している（実測: `SaaSConnector` / `WikiConnector` /
`DatabaseConnector` ほか）。

したがって境界は次のとおりである。**本作業は左側だけを実装する。**

| 本作業（契約側） | 対象外（発報側・実装済み or 別issue） |
| --- | --- |
| 健全性 3 項目を**永続化**し `DataSourceDto` へ載せる | 構造化ログでの発報（**実装済み**。本作業では**しきい値の値だけ**を計画へ合わせる） |
| しきい値（＝再試行上限）を契約に**明示**する | Alertmanager への配線（**未配備**。#546 / `docs/operations/operations.md` の既知の未了） |
| SC-06 で琥珀を表示する | 通知チャネル（メール等。`ADR-0045`・別issue） |

#### しきい値 —— **3 → 5 へ改める（計画が明示的に決めている）**

計画 §SC-06 は「**「継続失敗」のしきい値は再試行上限に達した時点とする**（hi-fi の「3/5」に倣えば
**5 回連続失敗**）。計画で決めないと実装が決めることになるため明示する」と書いている。

現行実装は `DataSourceSyncService.AlertThreshold = 3` である（実測）。**これは計画が明示的に
「実装が決めることになる」として排した状態そのもの**なので、**再試行上限 `5` へ改め、
しきい値と再試行上限を同一の定数に畳む**（2 つ持つと片方が黙って古くなる）。

> hi-fi の「3/5」は「**5 回中 3 回目**」という進捗表示であって、しきい値が 3 という意味ではない。
> 計画はその読み替えを明示している。**モックの数字をしきい値として写さない。**

#### 契約（追加は 3 項目・すべて省略可能）

```csharp
public record DataSourceDto(
    …既存の位置パラメータは順序も型も変えない…,
    DateTimeOffset? NextSyncAt = null,
    // ここから追加（FR-01, FR-02, UC-04, SC-06 / Q14）
    int ConsecutiveFailureCount = 0,
    int RetryLimit = DataSourceSyncHealth.DefaultRetryLimit,
    string? LastSyncError = null,
    DateTimeOffset? LastSyncErrorAt = null);
```

- **`ConsecutiveFailureCount`**: 連続失敗回数。成功で 0 に戻る。
- **`RetryLimit`**: 再試行上限（＝継続失敗のしきい値）。**画面が「3/5」の分母を契約から得るために返す**
  （画面へ定数を複写すると [[IADR-0127]] 決定 2 が禁じた「契約から導出できない表示」に戻る）。
- **`LastSyncError` / `LastSyncErrorAt`**: 直近エラー。**メッセージは秘密を含み得る**ため、
  既存の `RedactSecrets` と同じ考え方で**接続文字列らしき部分をマスクして保存する**（§セキュリティ）。

**`IsUnhealthy` のような導出フラグは契約に持たせない。** 判定（`ConsecutiveFailureCount >= RetryLimit`）は
画面側で行える。持たせると同じ判断が 2 箇所に立ち、片方が古くなる。

#### 永続化

健全性はエンティティ（`DataSource`）に持ち、EF の追加マイグレーション 1 本で列を足す。
**インメモリの `SyncFailureTracker` を読み口に流用しない。** 理由と代替案の棄却は
**[[IADR-0148]]** に記録する（要旨: プロセスローカルな計数は再起動で消え、読み取りがどのインスタンスへ
当たるかで値が割れる。計画が「静かに壊れる機能に気づく手段」と位置づけた表示の土台としては成立しない）。

### #534: 更新 API（Q16）

| 口 | 意味 | 認可 |
| --- | --- | --- |
| `PUT /datasources/{id}` | **全置換**。`Name` / `SourceType` / `ConnectionUri` / `Config` / `DefaultAttributes` を要求どおりに置く | 管理者限定 |
| `PATCH /datasources/{id}` | **部分更新**。`null` の項目は現状維持 | 管理者限定 |

- **`Id` / `CreatedAt` / `LastSyncedAt` / 健全性は更新の対象外**である。Q16 の目的は
  「**ID と履歴を切らない**」ことなので、更新で履歴を巻き戻せてはならない。
- **`DefaultAttributes` は機密区分のフェイルセーフを必ず通す**（`WithConfidentialityFailsafe`）。
  更新で `confidentiality` を空にできると、fail-closed 検索（[[IADR-0012]]）から文書が落ちる。
- **秘密の扱い**: 応答は既存どおり `RedactSecrets` を通す。**`PATCH` で `Config` を省略したときに
  マスク済みの値（`***`）が書き戻されない**こと（＝ 読んで書き戻す往復で秘密が破壊されないこと）を
  テストで固定する。
- **`disabled` なソースも更新できる**。無効化は論理削除であり、認証情報のローテーションは
  無効中にも起こる。

## 受け入れ基準

**#537（Q14）**

- [ ] `DataSourceDto` が連続失敗回数・再試行上限・直近エラー（メッセージと時刻）を持つ
- [ ] 連続失敗が**再試行上限（5）に達した時点**で継続失敗アラート（構造化ログ `Alert=true`）が出る
- [ ] 同期成功で連続失敗回数が 0 に戻り、直近エラーが消える
- [ ] 健全性がプロセス再起動をまたいで保持される（＝永続化されている）
- [ ] SC-06 が `ConsecutiveFailureCount >= RetryLimit` を**琥珀（warning）** で表示し、
      **色だけで意味を持たせない**（アイコン ＋ テキストを伴う。INDEX 決定 21）
- [ ] 直近エラーのメッセージに接続文字列由来の秘密が平文で載らない

**#534（Q16）**

- [ ] `PUT /datasources/{id}` と `PATCH /datasources/{id}` が在り、`/bff/datasources` からも呼べる
- [ ] 更新しても `Id` と `CreatedAt` と `LastSyncedAt` が変わらない（＝ ID と履歴を切らない）
- [ ] `PATCH` で省略した項目が現状維持される
- [ ] 更新で `confidentiality` を欠落させられない（フェイルセーフが働く）
- [ ] 存在しない ID は 404
- [ ] **更新は管理者限定**（運用者は 403）

**共通**

- [ ] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が**両ユニット**で通る
- [ ] `pnpm run typecheck` / `lint` / `format:check` / `test:coverage` / `build` が通り、カバレッジ床を割らない
- [ ] `node scripts/check-contract-schema.js` が**非破壊**と判定する（承認 allowlist を使わない）
- [ ] orval 生成物・Lingui カタログの再生成差分が無い
- [ ] `check-doc-links` / `check-adr-numbering` / `check-cross-repo-refs` / `check-plan-id-qualification` /
      `check-landed-subjects` が通る

## テスト方針

受け入れ基準を `[Fact]` / `[Theory]` と Vitest のケースへ 1 対 1 で写像する。

| 受け入れ基準 | テスト |
| --- | --- |
| しきい値 5 で発報・成功で 0 復帰 | `DataSourceSyncServiceTests`（既存の 3 回前提のケースを**再試行上限基準へ改める**） |
| 健全性の永続化 | `DataSourceService.Api.Tests` の同期後の再読み込み |
| 直近エラーの秘密マスク | `DataSourceSecretRedactionTests` へ追加 |
| PUT / PATCH の意味論・404・履歴不変 | 新規 `DataSourceUpdateEndpointTests` |
| 更新の管理者限定 | `DataSourceAuthorizationTests` へ追加（運用者で 403） |
| BFF の中継 | `BffDataSourceEndpointTests` へ追加 |
| 琥珀の表示 | `syncState.test.ts` ＋ `DataSourceManagementPage.test.tsx` |

**変異試験**（[[IADR-0141]] 決定 4）: 本作業は**機械検査を新設しない**ため必須ではない。ただし
しきい値の変更は「壊すと落ちる」を確かめる価値があるので、**しきい値を 4 に戻すと新テストが落ちる**ことを
実測して本仕様書へ記録する。

## 計画書との差異

- **差異: あり（1 件・本作業の対象外として別issue化する）**
  計画 §SC-06 §アクセス制御 は「**登録・更新・無効化は管理者限定**」と定めるが、現行実装は
  `POST /datasources` と `DELETE /datasources/{id}` が `admin` ＋ `operator` である（実測:
  `DataSourceEndpoints.cs:17-20` のグループ既定をそのまま使っている）。**本束は新設する
  `PUT` / `PATCH` を計画どおり管理者限定で作り、既存 2 口の差異には触れない**（[[IADR-0116]] 規約 4）。
  **別 issue を起票して送る**（同型の先例は #501 の再変換 API 管理者限定化）。
- それ以外の差異は無い。しきい値 3 → 5 は**計画への追随であって差異ではない**。

## 検証記録（すべて実走。**件数は `git add` の後に取った**）

**走査基準**: `develop` = `ae66549`。

```
dotnet build knowledge/backend/backend.slnx     Build succeeded / 2 Warning（既存 CS0618）/ 0 Error
dotnet build platform/backend/backend.slnx      Build succeeded / 0 Warning / 0 Error
dotnet test  knowledge/backend/backend.slnx     全 11 アセンブリ Passed（DataSourceService.Api.Tests 105 件）
dotnet test  platform/backend/backend.slnx      全 3 アセンブリ Passed（Platform.Bff.Tests 154 件 / skip 1）
dotnet format <両 slnx> --verify-no-changes     差分なし
pnpm run typecheck / lint / format:check        通過（lint は既存 warning 9 件・0 error）
pnpm run test:coverage                          Statements 96.38% / Branches 90.51% / Functions 91.7%（床 90/85/88 を維持）
pnpm run build                                  成功
pnpm run test:e2e                               13 passed
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js   293 tests passed
check-doc-links                 OK: 482 件
check-cross-repo-refs           OK: 559 件
check-plan-id-qualification     OK: 1189 件
check-landed-subjects           OK: 着地件名 328 件
check-adr-numbering             OK
check-i18n-catalogs             OK: 2 ロケール（ja / en）
check-contract-schema           OK: 2 プロジェクト / 62 型（**非破壊 7 件を baseline へ反映**・承認 allowlist は未使用）
check-test-spec-coverage        OK: 対 74 件（床を 70 → 74 へ更新）
check-chunk-budget --self-test  13 件すべて通過
check-chunk-budget --require    必須チャンク 3 本実在 / 初期ロード 577.92 kB（**床を 577.68 → 577.92 kB へ更新**）
check-static-egress --require   OK: dist 23 ファイルに外部オリジンなし
```

**床を動かした 3 件はいずれも意図した増加である**。理由を残す（黙って上げない）。

| 床 | 前 → 後 | 理由 |
| --- | --- | --- |
| `chunk-budget-baseline.json` | 577.68 → **577.92 kB**（+0.24 kB） | Lingui カタログへ 2 メッセージ（`再試行中（n/m）` / `同期異常（n/m）`）が増えた。カタログは初期ロードに載る |
| `contract-schema-baseline.json` | — | 契約の追加 7 件（**すべて非破壊**）。[[IADR-0122]] 決定 2 の破壊的側には該当しない |
| `test-spec-coverage-baseline.json` | 70 → **74 対** | テスト仕様書へ T-26〜T-36 と新規 2 クラスを記載した |

### 変異試験（テスト方針で予告したもの）

しきい値を再試行上限から切り離す変異を当て、**新テストが実際に落ちる**ことを確かめた。

| 変異 | 期待 | 実測 |
| --- | --- | --- |
| `AlertThreshold` を `DataSourceSyncHealth.DefaultRetryLimit` から `4` へ書き換える | 落ちる | **`Failed: 1`**（`Sync_RepeatedFailures_ReachAlertThresholdAtRetryLimit`） |
| 変異を戻す | 通る | `Passed: 105` |

### AI レビューの指摘と対応（PR #627・🔴 0 / 🟡 1 / 🟢 1）

| 指摘 | 自分での検証 | 対応 |
| --- | --- | --- |
| 🟡 **`PUT` / `PATCH` の `config` は全置換なので、応答のマスク値（`***`）を書き戻すと秘密が壊れる** | **実測で再現した。** `Update_WritingBackMaskedSecret_KeepsStoredValue` を先に書いたところ `PUT` / `PATCH` の**両方で fail** した（`apiToken` が `"***"` として永続化される） | **是正した。** `SecretConfigMask` を新設し、**マスク値は保存せず既存値を保つ**（[[IADR-0148]] 決定 6）。**繰り延べにしなかった理由**: 読んで書き戻すのは API の最も普通の使い方であり、「将来の編集フォームが踏む」ではなく**いま口を直接叩く運用者が踏む**。#534 が新設した口の欠陥なので本 PR の射程内である |
| 🟢 **差異の是正 issue の番号が本文に無い**（レビュー環境では issue 検索が不可） | 起票は済んでいた（レビュー実行時点では本文へ未反映） | **#628** を PR 本文・本仕様書・画面仕様書 §未決事項 5 へ明記した |

> **レビューが「繰り延べの判断自体は妥当」と述べた点には従わなかった。** 指摘の**事実**（往復で壊れる）は
> 正しいが、**繰り延べてよいかの評価**は自分で当て直した結果と違った ——
> 画面仕様書の「`config` を省略すれば安全」という注記は、**API を直接叩く経路を数えていない**。
> 契約だけ確定して防御を後回しにすると、**その間に壊れた資格情報は元に戻せない**。
> **他人の切り分けは受け入れ、他人の重み付けは自分で当てる。**

### 検証側で踏んだこと（記録）

- **`dotnet ef` が生成したマイグレーションに BOM が付き、`dotnet format` が `error CHARSET` で落ちた。**
  `.cs` と `.Designer.cs` の 2 本から BOM を除いて解消した。**生成物だからといって整形ゲートを免れない。**
- **件数を `git add` の前に測ると `check-cross-repo-refs` が 2 件少なく出る**（`git ls-files` 走査のため）。
  前セッションが実際に踏んだ型なので、**先に staging してから測った**（`doc-links` は
  `readdirSync` なので前後で変わらない）。

## 未決事項

- 無し（着手条件は満たしている）。**#537 の条件 B は §境界の確定 で解消した。**
- **別issue 化した事項が 1 件ある**（§計画書との差異）: `POST` / `DELETE /datasources` の認可が
  計画（管理者限定）より広い（admin ＋ operator）。本束の射程外として触れず、**#628** を起票した。
