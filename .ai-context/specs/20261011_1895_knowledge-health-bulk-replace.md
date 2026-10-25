---
title: ナレッジ健全性の報告（H-1）の受け口を一括の置換にし、4 万件でも gRPC の期限 5 秒に収める
type: spec
status: done
related_ids: [FR-10, FR-17, FR-18, SC-10, NFR-16, ADR-0002, ADR-0029, ADR-0075, IADR-0265, IADR-0353, IADR-0408]
author: claude
created: 2026-10-11
updated: 2026-10-11
plan_refs: []
issue: "#1895"
---

# 仕様書: ナレッジ健全性の観測値のスナップショット置換を一括にする（#1895）

## 起点

- 要求: `FR-10`（ダッシュボード）・`FR-17` / `FR-18`（健全性指標の供給）・`NFR-16`（east-west gRPC）。画面は `SC-10`。
- issue: #1895。2026-10-10 の PoC の h2c 往復の実測で、graph → dashboard の `KnowledgeHealthReport/Report`（H-1。1 時間周期）のうち
  `orphan-documents`（38,703 件）だけが毎周期 `DeadlineExceeded` になった。dashboard の要求ログは `499 … 5165.7368ms`。
- issue のコメント（実装側の見立て）が原因と是正案を示している。
  - 原因: `ReportKnowledgeHealthUseCase.ExecuteAsync`（`Features/KnowledgeHealth/Report/ReportKnowledgeHealthUseCase.cs:41-71`）が
    既存行を変更追跡つきで読み、`RemoveRange` / `AddRange` で 1 行ずつ DELETE / INSERT していた。
    処理は `context.CancellationToken` で取り消されるので、期限で毎周期ロールバックし、値が古いまま残っていたとみられる。
  - 是正案: 削除は `ExecuteDeleteAsync`、挿入も一括（COPY かバッチ挿入）、全体を 1 トランザクション、**必要なら**期限を延ばす。
  - 次の壁: 要求は約 1.7 MB。約 9 万件でサーバの受信上限 4 MB に当たる。分割も検討すること。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| 1 | SQL 文の数が観測値の件数に比例しない（10 件と 40,000 件で同数） | `KnowledgeHealthSnapshotPostgresTests.四万件の置換でもSQL文の数は十件のときと同じで期限内に終わる` |
| 2 | 40,000 件の置換が送信側の期限 5 秒に収まる | 同上（`Stopwatch` で 5 秒未満を固定） |
| 3 | 置換の途中（DELETE の後・INSERT の後）は、別の接続から旧い集合だけが見える | `置換の途中は別の接続から旧い集合だけが見える` |
| 4 | 途中で取り消されたら（期限切れ）旧い集合としきい値が残る | `挿入の後で取り消されたら旧い集合としきい値が残る` |
| 5 | 指標単位の置換・値の保存（null の区別・軸・切り詰め・空鍵の除外・しきい値の削除）は従前と同じ | `一括の経路でも指標単位の置換と値の保存は従前と同じである` ＋ 既存の `DashboardService.Tests`（InMemory）93 件 |
| 6 | 観測時刻が UTC 以外（Offset ≠ 0）でも落ちず、同じ瞬間として保存される | `観測時刻がUTC以外でも同じ瞬間として保存される` |

試験は `Knowledge.IntegrationTests/DashboardService/`（`Category=Integration`。実 PostgreSQL）に置いた。
`DashboardService.Tests` は EF InMemory であり、InMemory は `ExecuteDeleteAsync`・生 SQL・トランザクションのどれも持たない。
**関係 DB の経路は単体テストでは原理的に踏めない**ので、実 DB の試験で固定する。PR の `ci.yml` は `Category!=Integration` で外すため、
走るのは `integration.yml` である（下の §検証で手元の実 PostgreSQL 16 での実走を記録した）。

## 決定

### 1. 置換の書き方（`Infrastructure/Persistence/KnowledgeHealthSnapshotWriter.cs` を新設）

関係 DB のとき、件数によらず次の文だけを 1 つのトランザクションで流す。

1. `DELETE FROM "KnowledgeHealthObservations" WHERE "Indicator" = @p`（`ExecuteDeleteAsync`。行を読み込まない）
2. `INSERT INTO "KnowledgeHealthObservations" (…) SELECT t.id, … FROM unnest(@ids, @indicators, @subject_keys, @doc_scopes, @dimensions, @observed_ats) AS t(id, …)`
   （配列 6 本を 1 文で渡す。0 件なら流さない）。［2026-10-11 追記 / #1906 のレビュー］列の対応は位置で決まるので、
   INSERT の列リスト・SELECT の列・`unnest` の別名を同じ並びで明記した（`SELECT *` に頼らない）。
   観測時刻の配列は `ToUniversalTime()` で正規化する（Npgsql は `timestamptz` の配列に Offset 0 の値しか受け付けない）。
3. しきい値の 1 行を読み（`FirstOrDefaultAsync`）、追加・更新・削除のいずれか 1 文（`SaveChangesAsync`。変化が無ければ 0 文）

実測の文の数は 3 本（しきい値を持たない指標）で、10 件でも 40,000 件でも同じだった。

- **COPY ではなく `unnest` を採った。** COPY（`BeginBinaryImport`）は EF のトランザクションの外の API で、コマンドの傍受に乗らない
  （試験で文の数を数えられない）。`unnest` は `ExecuteSqlRawAsync` で EF の接続・トランザクション・傍受にそのまま乗る。
  4 万件で INSERT はサーバ側 0.4 秒（下表）であり、COPY にする理由が無い。
- **列名・表名は EF のモデルから引く**（`GetTableName` / `GetColumnName` / `ISqlGenerationHelper.DelimitIdentifier`）。
  移行で列名が変わったときに、生 SQL だけが古く残らないようにするためである。
- **値の正規化は従前の `KnowledgeHealthObservation.Create` を通す**（指標名の小文字化・鍵の切り詰め・DocScope の正規化・空白の軸を null）。
  受け口の規則を生 SQL 側へ写していない。
- **InMemory のときは従前の変更追跡の経路で書く**（`db.Database.IsRelational()` で分岐。`GraphSyncTransaction` と同じ形）。
  本番（PostgreSQL）では通らない。分岐を置かずに InMemory の試験を消す案は採らない —— 受け口の入力規則・置換の意味の 93 件が
  InMemory の上で回っている。

### 2. トランザクションと原子性

- `BeginTransactionAsync`（既定の READ COMMITTED）で 1〜3 を包み、最後に `CommitAsync` する。
- 読み手（閲覧の `GET /dashboard/knowledge-health`）は READ COMMITTED の文単位のスナップショットで読むので、コミット前の
  「消しただけ」「半分入れた」状態は見えない。旧い集合か新しい集合のどちらかだけが見える（受け入れ基準 3）。
- 期限切れ（gRPC の `context.CancellationToken`）・例外ではコミットせず、`await using` の Dispose がロールバックする。
  旧い集合としきい値がそのまま残る（受け入れ基準 4）。
- 実行戦略（`EnableRetryOnFailure`）は DashboardService では構成していない（`Program.cs` は素の `UseNpgsql`）。
  したがって `CreateExecutionStrategy` で包む必要は無い。再試行戦略を足すときは、この手動トランザクションを戦略で包むこと。

### 3. 期限（5 秒）は延ばさない

issue のコメントは「**必要なら**期限を延ばす」である。一括にした後の実測（下表）は 4 万件で 0.5〜0.8 秒であり、5 秒に対して 6 倍以上の余裕がある。
したがって延ばさない。延ばすと、受け口が本当に詰まったときに定期処理が止まる時間だけが伸びる。

### 4. 送信側（graph）の期限・再試行は変えない

- 期限は `GrpcKnowledgeHealthReporter.SendTimeout`＝5 秒のまま。
- **期限切れでも再送しない。次の周期（`KnowledgeHealthHostedService.Interval`＝1 時間）を待つ。** 報告は全量のスナップショット置換なので、
  次の周期の報告が取りこぼしを埋める。受け口は取り消されるとロールバックするので、期限切れは「旧い値のまま」であって「半端な値」ではない。
  同じ周期で再送すると、遅い受け口へ同じ重さを重ねるだけである。
- これは従前からの挙動であり、コードは変えていない。issue が「再送するのか、次の周期を待つのかを明記する」と求めたので、
  `GrpcKnowledgeHealthReporter` のコメントに明記した。

### 5. 要求の分割（受信上限 4 MB）は本 PR に入れない

- 1 件あたりの符号化は約 44 バイト（38,703 件で約 1.7 MB）。4 MB に当たるのは約 9.5 万件である。現在の 38,703 件の 2.4 倍にあたる。
- 分割は、本 PR が守る「指標 1 つ分を 1 回で全量置換する（原子性）」と両立しない。複数の要求に分けるなら、
  世代番号つきの書き込みと最後の要求での切り替え（または client streaming）が要り、契約（proto）と送信側の両方を変える。
  期限の是正とは別の設計判断なので、別 issue に送る（§残課題）。

## 実測（手元・PostgreSQL 16.13・ホストの負荷平均 50 前後）

`KnowledgeHealthSnapshotPostgresTests` を `PLATFORM_TEST_POSTGRES` で手元の PostgreSQL 16 に向けて実走した。
旧い集合 40,000 件を置いたうえで、新しい 40,000 件で置換した。

| 経路 | 40,000 件の置換の所要 | SQL 文の数（40,000 件 / 10 件） |
| --- | --- | --- |
| 従前（変更追跡・`RemoveRange` / `AddRange`。変異確認で強制） | **6,181 ms** | **82 本** / 3 本 |
| 本 PR（一括 DELETE ＋ `unnest` INSERT ＋ トランザクション） | **495〜791 ms**（5 回） | **3 本** / 3 本 |

- 従前の 6.2 秒は、稼働で観測した 5.2 秒以上（期限で打ち切り）と整合する。
- 1 回だけ 17 秒が出た。そのときディスクの空きが 0 に近かった（`df` で 100%）。空きを作った後の 5 回は 495〜791 ms である。
  WAL の書き込みが詰まったとみる。稼働の DB はこの状態ではない。
- サーバ側の内訳（`EXPLAIN ANALYZE`）: 40,000 行の DELETE が 109 ms、`unnest` の INSERT が 401 ms。
- **時間の見積もり**: どちらの文も行数に線形である。約 9.5 万件（受信上限の壁）でも、DB 側は 0.3 秒＋1.0 秒程度、全体で 2 秒前後と見積もる。
  期限 5 秒に収まる。gRPC の受信（約 4 MB の解析）と `Create` の 9.5 万回は合わせて数十〜百ミリ秒の規模である。

## 母集合（規則 9・10）

- 誤りの側の文字列で走査した: `git grep -n "RemoveRange\|KnowledgeHealth/Report" -- 'src/knowledge/backend/Services/DashboardService'`。
  `UsageEventRetention.cs` のコメントが「同じサービス内の前例（`KnowledgeHealth/Report`）も `RemoveRange` である」と書いていた。
  本変更で誤りになるので直した。`UsageEventRetention` 自身は 500 件の周回であり、変更追跡のままでよい（変えない）。
- `ReportKnowledgeHealthUseCase` を直接呼ぶのは REST の端点と gRPC の面だけである（`git grep -n ReportKnowledgeHealthUseCase`）。
  両方が同じ本体を通るので、片方だけ直る状態は無い。
- 並行作業の #1891・#1871 のファイルには触れていない。

## 変更したファイル

| ファイル | 変更 |
| --- | --- |
| `DashboardService/Infrastructure/Persistence/KnowledgeHealthSnapshotWriter.cs` | 新設（§決定 1・2） |
| `DashboardService/Features/KnowledgeHealth/Report/ReportKnowledgeHealthUseCase.cs` | 置換を上へ委ねる。検証・正規化・戻り値は不変 |
| `DashboardService/Features/Dashboard/PurgeExpired/UsageEventRetention.cs` | コメントの前例の記述を直す（規則 10） |
| `GraphService/Infrastructure/ExternalServices/GrpcKnowledgeHealthReporter.cs` | コメントのみ（再送しない・期限を延ばさない） |
| `Tests/Knowledge.IntegrationTests/Knowledge.IntegrationTests.csproj` | `DashboardService` を参照 |
| `Tests/Knowledge.IntegrationTests/DashboardService/KnowledgeHealthSnapshotPostgresTests.cs` | 新設（受け入れ基準 1〜6） |
| `DashboardService/DashboardService.csproj` | `InternalsVisibleTo Knowledge.IntegrationTests`（受け入れ基準 6 で書き手を直接呼ぶ。受け口は `UtcNow` しか渡さない） |

IADR は作らない（判断は本書に置く。期限・再試行の方針は従前どおりで、新しい設計判断は書き方の選択だけである）。

## 配備後の確認（PoC・2026-10-12）

1. graph の次の周期（起動から 1 時間後）で、`indicator=orphan-documents` の `ナレッジ健全性の報告に失敗した（status=DeadlineExceeded）` が出ない。
2. dashboard の要求ログで、`KnowledgeHealthReport/Report` の 5 件が `200`、所要が 5 秒を大きく下回る（目安 2 秒未満）。`499` が無い。
3. graph の sidecar（`reporter=source`）で `graph-service → dashboard-service` の gRPC 状態が 5 件とも 0。
4. `GET /dashboard/knowledge-health` の `orphan-documents` の件数が、graph のログの `count=` から個人資料を除いた値に一致し、報告ごとに更新される。
5. dashboard の DB で `SELECT "Indicator", count(*), max("ObservedAt") FROM "KnowledgeHealthObservations" GROUP BY 1;` を流し、
   `orphan-documents` の `max("ObservedAt")` が最新の周期の時刻になっている（毎周期ロールバックしていた間は古いまま）。

## 残課題

- **要求の分割（受信上限 4 MB）**: 約 9.5 万件で当たる。原子性を保った分割（世代つき書き込み・client streaming）か、
  この rpc だけ受信上限を明示的に上げる（#1897 の D-1 と同じ応急処置）かを決める。別 issue で扱う。
- **閲覧の 2 文の間の食い違い**: 閲覧は観測値としきい値を別の文で読む。報告のコミットがその間に挟まると、
  観測値は旧い集合・しきい値は新しい値、という組み合わせが 1 回だけ見え得る。従前からある性質で、しきい値は日数の表示だけに使うので本 PR では扱わない。
- **同じ指標の報告が同時に 2 本来た場合**: READ COMMITTED では、後発の DELETE が先発の挿入した行を消さず、両方の集合が残り得る。
  従前の経路も同じ性質を持つ。生産者は graph のリース（`IKnowledgeHealthLeaseCoordinator`）で 1 本に絞られているので、本 PR では扱わない。
