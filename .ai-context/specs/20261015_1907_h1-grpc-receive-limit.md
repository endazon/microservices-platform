---
title: ナレッジ健全性の報告（H-1）を 15 万件規模でも受信上限と期限に収める —— 受け口の受信上限を rpc 単位で 16 MiB に明示し、期限を 30 秒へ延ばし、送信側でサイズを測って警告する
type: spec
status: done
related_ids: [FR-10, FR-17, FR-18, NFR-16, ADR-0029, ADR-0075, IADR-0389, IADR-0408, IADR-0539, IADR-0542]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs: []
issue: "#1907"
---

# 仕様書: H-1 の受信上限と期限（#1907）

## 起点

- 要求: `FR-10`（ダッシュボード）・`FR-17` / `FR-18`（健全性指標の供給）・`NFR-16`（east-west gRPC）。計画 ADR は `ADR-0029`・`ADR-0075`。
- issue: #1907（#1895 の残課題）。H-1（graph → dashboard の `KnowledgeHealthReport/Report`）は指標ごとの観測を 1 要求で全量送る。
  受け口の受信上限は grpc-dotnet の既定 4 MB で、約 9.5 万件で `ResourceExhausted` になる。PoC は再索引で台帳が伸びている
  （切り替え後だけで 39,780 件、台帳は 4.5 万件超）。
- 前例: #1899（D-1 の受信上限を構成キーで明示。作業仕様書 `20261010_1897_grpc-receive-limit-stopgap`）。
  親: #1895・#1906（受け口の一括置換。作業仕様書 `20261011_1895_knowledge-health-bulk-replace`）。

## 受け入れ基準（issue）と試験の写像

| # | 基準 | 試験・記録 |
| --- | --- | --- |
| 1 | 採る案を決め、IADR（案 2・3 の場合）または作業仕様書に根拠を残す | 案 1 を採ったが、#1895 の判断（期限 5 秒）を改めるので **IADR-0542** に残した。本書は実測・母集合・検証を持つ |
| 2a | 15 万件規模で H-1 が**受信上限**に収まる | `KnowledgeHealthReportReceiveLimitTests.十五万件の報告は既定の4MBを超えるが受け口に受理される`（実 Kestrel の h2c で往復。サイズは 4 MB 超・16 MiB 未満を固定）／陰性 `受信上限を超える報告はResourceExhaustedで拒まれる` |
| 2b | 15 万件規模で H-1 が**期限**に収まる | `KnowledgeHealthSnapshotPostgresTests.十五万件の報告も受信上限と期限の両方に収まる`（実 PostgreSQL。線上のバイト列の解析 → 写し → 置換の合計を、期限 30 秒の 1/3＝10 秒未満で固定） |
| 補 | 受信上限を超える手前で警告し、成功のたびに件数・サイズ・所要を残す | `GrpcKnowledgeHealthReporterTests.受理されたら件数とサイズと所要を残す`・`要求が受信上限の75パーセントを超えたら警告する`（閾値の上下の対）・`警告の閾値は受け口の既定の受信上限の75パーセントである`・`送出の期限は30秒である` |
| 補 | 構成キーの読み方（未設定は既定・不正値は起動失敗） | `KnowledgeHealthReportReceiveLimitTests.構成が無ければ既定を使い_正の整数なら採る`・`不正な構成は構成キーを名指して落ちる` |

## 決定（要約。正は IADR-0542）

1. **案 1 を採る。** 受け口のサービス（`KnowledgeHealthReportGrpcService`）にだけ受信上限 16 MiB を与える
   （`GrpcServiceOptions<T>`。構成キー `Grpc:KnowledgeHealthReportMaxReceiveMessageSize`、env `Grpc__KnowledgeHealthReportMaxReceiveMessageSize`）。
   不正値は起動時に落とす（#1899 と同じ）。helm values には描画しない（既定で足りる。#1899 と同じ）。
2. **送信側の期限を 5 秒から 30 秒へ延ばす**（#1895 §決定 3 を改める）。受信上限だけを直すと、症状が `DeadlineExceeded` に変わるだけである（下の実測）。
3. **送信側は送る前に要求の符号化サイズを測り、12 MiB（既定の 75%）以上で Warning、受理のたびに Information で件数・バイト数・所要を残す。**
4. 案 2（世代つき分割）・案 3（client streaming）は採らない。契約は変えない。

### 実装の要点

- `AddKnowledgeHealthReportReceiveLimit()` は**先に `AddGrpc()` を呼ぶ**。grpc-dotnet はサービス別の既定を全体の値の写しとして
  `AddGrpc()` の `IConfigureOptions` で入れるので、それより前に登録した Configure は 4 MB で上書きされる。
- 構成は DI の `IConfiguration` から遅延で読み、`ValidateOnStart` で起動時に 1 度解決する（`WebApplicationFactory` の構成も見える）。
- 送信側の `CalculateSize()` は要求の組み立て後・送出前に 1 回だけ呼ぶ。失敗の Error ログにも同じバイト数を載せる。

## 実測（手元・PostgreSQL 16.13・ホストの負荷平均 16〜50。他の作業と共用）

最も重い形 = 解決できないリンク（鍵は graph の `UnresolvedLinkKey`：32 桁 ＋ ":" ＋ 32 桁の 65 文字）に軸 `not-found` と個人資料の印を全件に付けたもの。

| 形 | 1 件の符号化 | 15 万件 | 16 MiB に入る件数 |
| --- | --- | --- | --- |
| 最も重い（解決できないリンク・軸・個人資料） | 94 バイト | **14,100,018 バイト** | 約 17.8 万件 |
| 文書 ID だけ（孤立文書・古い文書） | 約 40 バイト | 約 6.0 MB | 約 42 万件 |

| 経路（15 万件・最も重い形・旧い集合も 15 万件） | 解析 | 合計 | DELETE 完了 | INSERT 完了 | 割り当て |
| --- | --- | --- | --- | --- | --- |
| 負荷平均 16〜23 のとき（4 回） | 141〜240 ms | **2,542〜2,714 ms** | 528〜645 ms | 2,523〜2,675 ms | 74〜80 MiB |
| 負荷平均 30〜50 のとき（6 回） | 157〜891 ms | **3,010〜6,370 ms** | 1,036〜1,580 ms（2 回だけ計測） | 3,009〜6,344 ms（同） | 74〜80 MiB |

- 合計は「線上のバイト列の解析（`ReportRequest.Parser.ParseFrom`）→ `ToCommand` → `ReportKnowledgeHealthUseCase`（検証・正規化・DELETE・`unnest` INSERT・しきい値・コミット）」。
  h2c の転送そのものは同一ホストで数十ミリ秒であり含めない。
- **期限 5 秒は負荷が高いと超える**（6.3 秒・6.4 秒を実測）。30 秒なら最悪の実測でも 4.7 倍の余裕がある。試験の門は 10 秒（期限の 1/3）。
- 割り当て（`GC.GetTotalAllocatedBytes` の差）は 74〜80 MiB。受け口の limits 512Mi・GC ヒープの上限 60%（≈307 MiB。IADR-0539）に対して約 1/4。
- 実 Kestrel の h2c で 15 万件（14.1 MB）を往復できること・上限を超える 19 万件弱（約 17.7 MB）が `ResourceExhausted` になることを確かめた
  （受け口は EF InMemory なので所要は測らない。往復は 11 秒前後で、ほぼ InMemory の変更追跡の時間）。

## 母集合（規則 9・10）

- 誤りの側の文字列で走査した: `git grep -n -E "期限 ?5 ?秒|5 秒の期限|9\.?5 万件|受信上限 ?4 ?MB" -- ':!.ai-context/specs' ':!CHANGELOG.md'`。
  - `GrpcKnowledgeHealthReporter.cs` の「期限 5 秒は延ばさない」→ 改めた旨の追記に置き換えた（規則 10）。
  - `KnowledgeHealthSnapshotPostgresTests.cs` の「4 万件が期限 5 秒に収まる」→ 「#1895 当時の期限」と明記し、4 万件の試験は 5 秒の門のまま残した
    （一括化の効果の固定。30 秒へ緩めると従前の 6.2 秒の経路でも緑になる）。
  - `IADR-0537`・`DocumentReadUseCase.cs` の「4 MB」は D-1（文書一覧）の話で、本件の対象外。
- `git grep -n -E "KnowledgeHealthReport|受信上限|deadline|期限" -- docs`: `docs/api/east-west-grpc.md` §6 つ目の面が「送出のタイムアウト（5 秒）」と書いていた。
  30 秒への変更と受信上限の段落を追記した（trace ブロックに IADR と issue を足した）。
- `GrpcKnowledgeHealthReporter.SendTimeout` の参照（`git grep -n SendTimeout`）: 送信側の本体と試験だけ。`GrpcPrivateNoteNotifier` / `UsageEventDispatcher` の
  同名の定数は別の経路で、変えない。
- 確定済みの #1895 の作業仕様書は書き換えない（point-in-time の記録）。判断の改めは IADR-0542 に置いた。
- helm chart: dashboard の env に受信上限は描画しない（既定で足りる）。chart は変えないので helm lint / template は走らせていない。

## 変更したファイル

| ファイル | 変更 |
| --- | --- |
| `DashboardService/Features/KnowledgeHealth/Report/KnowledgeHealthReportReceiveLimit.cs` | 新設（構成キー・既定 16 MiB・読み方・登録） |
| `DashboardService/Program.cs` | 登録を 1 行足す |
| `GraphService/Infrastructure/ExternalServices/GrpcKnowledgeHealthReporter.cs` | 期限 30 秒・`PayloadWarnBytes`・成功の Information・警告・失敗ログのバイト数 |
| `DashboardService/Tests/Features/KnowledgeHealth/Report/KnowledgeHealthReportReceiveLimitTests.cs` | 新設（h2c の陽性・陰性・構成） |
| `GraphService/Tests/Infrastructure/ExternalServices/GrpcKnowledgeHealthReporterTests.cs` | 成功のログ・警告の上下・閾値の一致・期限 |
| `Tests/Knowledge.IntegrationTests/DashboardService/KnowledgeHealthSnapshotPostgresTests.cs` | 15 万件の試験を足す。4 万件の門を「#1895 当時の 5 秒」として分けた |
| `docs/api/east-west-grpc.md` | §6 つ目の面に期限と受信上限を追記 |
| `.ai-context/adr/IADR-0542_*.md`・`README.md` | 新設・索引 |

## 検証

- `dotnet test DashboardService.Tests.csproj` → 101 件成功。`dotnet test GraphService.Tests.csproj` → 841 件成功。
- `PLATFORM_TEST_POSTGRES=… dotnet test Knowledge.IntegrationTests --filter KnowledgeHealthSnapshotPostgresTests` → 6 件成功（手元の PostgreSQL 16.13）。
- `dotnet format backend.slnx --verify-no-changes --include <変更ファイル>` → 差分なし。
- **変異**: `Program.cs` の `AddKnowledgeHealthReportReceiveLimit()` を外す → `十五万件の報告は既定の4MBを超えるが受け口に受理される` が
  `ResourceExhausted`（"Received message exceeds the maximum configured message size."）で赤。戻して緑。
- 期限 5 秒のまま 15 万件の試験を走らせると、負荷の高いときに 6.3 秒で赤になった（上の実測。期限を延ばす根拠）。

## 配備後の確認（PoC）

1. dashboard の Pod が起動する（構成キーを与えていなければ既定 16 MiB。与えた値が不正なら起動失敗で止まる）。
2. graph の次の周期（起動から 1 時間後）で、5 指標それぞれに
   `ナレッジ健全性の報告が受理された。indicator=… count=… bytes=… elapsedMs=…` が出る。
   `kubectl logs deploy/graph-service -c graph-service | grep 'ナレッジ健全性の報告'`。
   - `bytes` が 4,194,304 未満なら従前の上限でも通っていた規模である。`elapsedMs` は数秒以下が目安（30 秒の期限に対する余裕）。
   - Warning（`受信上限に近い`）が出ていない（12 MiB 未満）。出たら分割の設計に進む（IADR-0542 §残余リスク）。
   - `ナレッジ健全性の報告に失敗した（status=ResourceExhausted|DeadlineExceeded）` が出ない。
3. dashboard の要求ログで `KnowledgeHealthReport/Report` が 5 件とも 200。
4. `GET /dashboard/knowledge-health` の各指標の件数が、graph のログの `count=` から個人資料を除いた値に一致し、周期ごとに更新される。
   DB では `SELECT "Indicator", count(*), max("ObservedAt") FROM "KnowledgeHealthObservations" GROUP BY 1;` の `max` が最新の周期の時刻。

## 残課題

- 台帳がさらに伸び、送信側の Warning（12 MiB）が出始めたら、案 2（世代つき分割）を設計する。上限を env で一時的に上げることはできるが、
  受け口のメモリ（limits 512Mi）とのトレードオフになる。
