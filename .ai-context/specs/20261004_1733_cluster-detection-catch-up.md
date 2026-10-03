---
title: 日次のクラスタ検出を、前回の成功からの経過で決め、再起動のたびに 24 時間を数え直さないようにする（#1733）
type: spec
status: done
related_ids: [FR-17, FR-18, FR-10, SC-10, SC-18, ADR-0035, ADR-0083, ADR-0076, IADR-0496, IADR-0425, IADR-0430, IADR-0299, IADR-0389]
author: claude
created: 2026-10-04
updated: 2026-10-04
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0035 決定 3（クラスタ検出は定期バッチ・日次）
  - planning:projects/microservices-platform/07_adr/ADR-0076 決定 3（沈黙を観測できる系列）
issue: "#1733"
---

# 仕様書: 日次のクラスタ検出の取りこぼしを追いつく（#1733）

> 本仕様書は実装着手前に作成する。起点は #1733（稼働 PoC 2026-10-03 の実測）。判断の記録は **IADR-0496** に置く。

## 起点となる計画書（トレーサビリティ）

- 要求: **FR-17**（知識グラフ）。クラスタは SC-18 の表示と、SC-10 の未要約クラスタ数（FR-10 / FR-18。#1396）の材料である。
- 計画 ADR: ADR-0035 決定 3「実行タイミングは定期バッチ（日次）」。**周期 24 時間は変えない**（本件は「日次が実際に回ること」の是正）。
  ADR-0076 決定 3（止まったことが観測できる系列を持つ）。
- 関連 IADR: IADR-0425（検出の実装・決定 6 の排他リース）、IADR-0430（要約の日次バッチ。同じ形）、IADR-0299 決定 3（定期処理のループの作法）。
- 採番: `origin/develop` `b0eaeff7` の最大は IADR-0495。開いている PR（#1735・#1726）は IADR を足さない。本件は **0496**。

## 何が起きたか（#1733 の実測）

- `graph_svc`: documents=20,290、edges=0、graph_clusters=6,107、**max(DetectedAt)=2026-09-27 03:12:19Z**。以後検出の痕跡なし。
- `ClusterDetectionHostedService` は `PeriodicTimer(1 日)` で、**初回が起動の 24 時間後**。PoC はほぼ毎日 Pod が作り直されるので、拍は永久に来ない。

## 直し方（決定。却下案と理由は IADR-0496）

1. **前回の成功時刻を永続化する。** 新しい表 `graph_batch_runs`（列は `JobName` 主キー・`LastSucceededAt`（NULL 可）・`LastAttemptedAt`・`AttemptsSinceSuccess`。後ろの 2 つは 4 の試行の記録）。検出ジョブが**クラスタの変更と同じ `SaveChanges`** で
   自分の行（`cluster-detection`）を書く —— 失敗・取り消しの周期は 1 行も書かず、成功も記録しない。記録する時刻はその周期の開始時刻（＝クラスタの `DetectedAt` と同じ値）。
   - `max(DetectedAt)` は使えない（IADR-0496 §検討した選択肢）: 構成が変わらない再検出は時刻を進めない（IADR-0425 の不変条件）ので、検出が毎日成功していても止まって見える。
     文書 0 件ならクラスタも 0 行で、時刻そのものが無い。
2. **起動の後、短い待ち（`ClusterDetection:StartupDelay`、既定 2 分）の後に 1 度判定する。** 前回の成功から 24 時間以上たっていれば（または記録が無ければ）すぐ走らせ、
   そうでなければ「前回 + 24 時間」まで眠る。以後も同じ判定を繰り返す（**位相は前回の成功に付く**。再起動は位相を動かさない）。
   前回の成功が許容（5 分）を超えて未来なら、壊れた記録としてすぐ走らせ、今の時刻で上書きする。
3. **判定はリースの内側で、行を読み直してから行う。** ローリング更新で新旧 2 Pod が同時に起きても、後から入った側は先の成功を見て走らない（二重実行の防止）。
4. **リースが取れないときは `ClusterDetection:RetryDelay`（既定 1 時間）後に判定し直す。周期が失敗した（取り消し含む）ときは、連続した失敗の数だけ待ちを倍々にする（24 時間で頭打ち）。**
   失敗は成功として記録しないので、次の判定は「まだ期限切れ」を見る。**試行は本体の前に別の保存で記録し**（`LastAttemptedAt`・`AttemptsSinceSuccess`）、
   判定は「成功の期限」と「最後の試行 + バックオフ」の長い方を待つ —— プロセスごと落ちた試行（OOMKill）も次の起動がバックオフに数える。
5. **観測**: 計器 `graph.cluster_detection.last_success.timestamp`（単位 `s`。Prometheus では `graph_cluster_detection_last_success_timestamp_seconds`）。
   値は前回の成功の Unix 秒。**記録が無い・まだ読めていない間は系列を出さない**（0 を出すと「1970 年に成功した」と読めてしまう）。Meter は既存の `microservices-platform.graph-service`。
   遅れは `time() - graph_cluster_detection_last_success_timestamp_seconds` で読む。アラート規則は本件では足さない（フォローアップ）。
6. 構成は既定へ倒す（負の `StartupDelay`、0 以下の `RetryDelay`、どちらも 1 日超は既定値。`ValidateOnStart` は付けない —— 購読ごと止めない。`ClusterSummaryOptions` と同じ向き）。

## 母集合（規則 9・10）

走査は 2026-10-04・`origin/develop` = `b0eaeff7`。

- 軸 A（誤りの側＝「起動から 1 周期後に初回」の日次処理）: `git grep -n "new PeriodicTimer" -- 'src/**/*.cs' ':!**/Tests/**'` = 11 行。
  | 箇所 | 周期 | 初回 | 扱い |
  | --- | --- | --- | --- |
  | GraphService `ClusterDetectionHostedService` | 1 日 | 1 周期後 | **是正（本件）** |
  | GraphService `ClusterSummaryHostedService` | 1 日 | 1 周期後 | **同型の欠陥。既定オフ**（`ClusterSummary:Enabled=false`）なので本件では触らない。フォローアップ（IADR-0496 §フォローアップ） |
  | DocumentService `PrivateNoteMaintenanceHostedService` | 24 時間 | 1 周期後 | **同型の欠陥**（コメントは「最悪 24 時間の遅延」と読むが、毎日再起動では無限）。別サービス・別 FR（FR-19/20/22）なので別 issue へ送る |
  | GraphService `KnowledgeHealthHostedService` | 1 時間 | 1 周期後 | 受容（再起動の間隔より十分短い） |
  | DashboardService `UsageRetentionHostedService`・Shared `DriftDetectionHostedService`・DataSourceService `DataSourceSyncHostedService` | — | 起動直後（do-while） | 対象外 |
  | AuthorizationService `DepartmentAttributeSync`（1 時間）・`OwnerReadPolicyCheck`（1 分）・NotificationService 保守（5 分）・DocumentService `ContentAbacGate`（再試行） | 短い | — | 対象外 |
- 軸 B（本件で新たに誤りになる自分の記述）: `git grep -n "初回は 1 周期後\|PeriodicTimer" -- src/knowledge/backend/Services/GraphService` と `.ai-context/adr/IADR-0425*`。
  - `ClusterDetectionHostedService.cs` 冒頭（「初回は 1 周期後」）→ 書き換える。
  - `ClusterSummaryHostedService.cs` 冒頭（「形は `ClusterDetectionHostedService` に合わせる（… PeriodicTimer ＋ 初回は 1 周期後）」）→ もう合っていないので、検出が変わったことと残る欠陥を注記する。
  - `BatchLoopForeignCancellationTests.cs` の検出の 2 件（周期＝拍の前提）→ 起動の待ち・再試行の待ちで同じ性質を測る形へ直す。`ClusterDetectionTests` の T-18 / T-18b → 戻り値の型が変わるので追随。
  - IADR-0425 の本文は凍結。日付つき追記で IADR-0496 を指す。
  - `docs/tests/FR-10_dashboard.md` の `BatchLoopForeignCancellationTests` の行は「次の拍まで待つ」を述べる。検出は「再試行の待ちまで待つ」になるが、性質（間を空けずに再試行しない）は同じなので本文は直さない。

## 受け入れ基準 → 試験（`docs/tests/FR-17_knowledge-graph.md` T-70〜T-79）

| ID | 基準 | 試験 |
| --- | --- | --- |
| T-70 | 期限の計算: 記録なし・24 時間以上前はすぐ、24 時間未満は残りだけ待つ、許容内の未来の記録は 1 周期で頭打ち、許容を超えて未来の記録はすぐ | `ClusterDetectionScheduleTests` |
| T-71 | 検出の成功は自分の行に周期の開始時刻を書く。構成が変わらない再検出でも時刻は進む（`DetectedAt` は進まない対照つき）。文書 0 件でも書く | `ClusterDetectionCatchUpTests` |
| T-72 | クラスタの保存が失敗した周期は成功を記録しない（クラスタも 1 行も残らない） | 同上 |
| T-73 | 記録が無い・24 時間以上前なら、起動の待ちの直後に検出が走る。起動の待ちより前には走らない | 同上 |
| T-74 | 24 時間未満なら起動では走らず、前回 + 24 時間で走る（その 1 分前には走っていない） | 同上 |
| T-75 | リースの内側で読み直す: 他の Pod が成功を記録した後にリースを取っても走らない。リースが取れない・失敗した周期は再試行の待ちの後に判定し直す | 同上・`BatchLoopForeignCancellationTests` |
| T-76 | 最後の成功の計器: 記録を読むまで系列を出さず、読んだら Unix 秒を出す。構成の既定・不正値（上限超えを含む）の既定への倒れ・構成からの束縛。マイグレーションがモデルと一致する | 同上・`ClusterDetectionMetricsTests` |
| T-77 | 失敗が続けば再試行の待ちを倍々（1・2・4 時間。周期とは別の値）。成功しなかった試行の記録が残っていれば起動の後もバックオフを待つ。本体が失敗した判定は試行だけを記録する | `ClusterDetectionCatchUpTests`・`ClusterDetectionScheduleTests` |
| T-78 | 本体の保存は 1 回。成功後の待ちは周期の開始時刻から 1 周期で、下限は再試行の待ち | `ClusterDetectionCatchUpTests` |
| T-79 | 停止要求は走っている判定へ届き、ループは静かに終わる | 同上 |

## 変異試験の結果

2026-10-04 実施。変異ごとに本体を書き換え、`dotnet test GraphService.Tests --filter "ClusterDetection|BatchLoop"`（42 件）を回して戻した。**9 件すべて殺した。**

| # | 変異 | 結果 | 殺した試験（主なもの） |
| --- | --- | --- | --- |
| M1 | 初回を起動の待ちではなく 1 周期後にする（#1733 の形） | 殺した | T-73（3 通り）・T-74 |
| M2 | 記録を見ず毎回すぐ走らせる（再起動ごとに走る） | 殺した | T-70（4 件）・T-74・T-75 |
| M3 | 成功の記録を検出の前に別の保存で書く | 殺した | **T-72（失敗の周期）**・T-71・T-73・既存の検出試験 |
| M4 | 構成が 1 つも変わらない周期は記録しない | 殺した | T-71（2 件） |
| M5 | 期限の記録をリースの外で読む | 殺した | **T-75（他の Pod の成功）**・`BatchLoopForeignCancellationTests` の検出 2 件 |
| M6 | 失敗の直後に待たず判定し直す | 殺した | `BatchLoopForeignCancellationTests`「失敗が続いても次の拍まで待って回す」（検出） |
| M7 | 記録が無いとき計器に 0 を出す | 殺した | T-76 の計器 2 件 |
| M8 | 未来の記録を 1 周期で頭打ちにしない | 殺した | T-70「未来の記録は 1 周期で頭打ち」 |
| M9 | リースが取れないとき再試行の待ちではなく 1 周期を返す | 殺した | T-75「リースを取れない判定は再試行の待ちを返す」・T-18 |

試験側の誤り 1 件を変異の前に直した（T-70 の入力が「1 分前」を「1 時間前」と取り違えていた。期待値は 23 時間のまま、入力を 60 分へ）。

## 独立監査の反映（2026-10-04・MSP#1738 の条件付き GO）

最初のコミット `ef3059d1` に対する独立監査の指摘を、同じブランチの 2 つ目のコミットで直した。

| 指摘 | 直し方 |
| --- | --- |
| 🟡1 未来の記録の頭打ちは「眠り続けない」を実現していない（3 日先なら 4 日走らない） | 許容（5 分）を超えて未来の成功の記録は、すぐ走らせて上書きする。許容内は従前どおり 1 周期で頭打ち |
| 🟡2 PoC の初回は大きい保存で、graph に resources が無い。OOM で成功が記録されず、再起動ごとに 2 分後の再実行が続く | 連続失敗の指数バックオフ（1 時間から倍々・24 時間で頭打ち）。試行を本体の前に別の保存で記録し、プロセスを跨いでもバックオフする。IADR-0496 に残余「初回配備後にメモリと所要時間を観測」。`resources.limits` は別の判断（本件では触らない） |
| 🟡3 試験の穴 N7・N12・N2/N3・N16 | T-77（再試行の待ちが周期と別の値）・T-78（保存回数 1 回・開始時刻基準・下限）・T-79（停止のトークン）を足し、各変異が赤になることを確かめた |
| 🟡4 統合試験の `GraphServiceFactory` が起動の待ちを延ばしていない | `ConfigureWebHost` で `UseSetting("ClusterDetection:StartupDelay", "1.00:00:00")` |
| 🟢 計器のコメント「どのレプリカが読んでも同じ」 | Pod ごとの値であり、アラートは `max()` で読む、と正した |
| 🟢 `Task.Delay` の上限を超える構成値 | 待ちの上限を 1 日にし、超えた値は既定へ倒す |
| 🟢 仕様書の列名 | 実装（`JobName` 等）に合わせた |
| 🟢 `RecordLastSuccess` の 2 回目 | 「書いた値の流用（読み直さない）」とコメントした |

### 変異試験の結果（2 回目）

`dotnet test GraphService.Tests --filter "ClusterDetection|BatchLoop"`（60 件）。**17 件すべて殺した。** M1〜M7・M9 は上の表と同じ内容を新しい実装に当て直したもので、すべて殺した。
M8（未来の記録を頭打ちにしない）は、頭打ちの意味が 🟡1 で変わったので N1 に置き換えた。

| # | 変異 | 殺した試験 |
| --- | --- | --- |
| N1 | 許容を超えた未来の記録でも 1 周期眠る（🟡1 の形） | T-70「許容を超えて未来の記録はすぐ走らせる」（6 分・3 日） |
| N7 | 失敗の後の待ちを周期にする | T-77「失敗が続くと再試行の待ちを倍々にする」 |
| N8 | 失敗の待ちを倍々にしない | 同上 |
| N9 | 永続化された試行を判定で見ない | T-77「成功しなかった試行の記録が残っていれば起動の後もバックオフを待つ」・T-77 の純関数 |
| N10 | 試行を記録しない | T-77「本体が失敗した判定は試行だけを記録する」 |
| N12 | 成功の記録をクラスタの後の別の保存にする | T-78「検出の本体は保存を 1 回だけ行う」（初回の変異の錨が試行の記録の側に当たって生存したため、錨を直して赤を確認した） |
| N2 | 成功の後の待ちの下限を外す | T-78「成功の後の待ちの下限は再試行の待ちである」 |
| N3 | 成功の後の待ちを周期そのものにする | T-78「成功の後の待ちは周期の開始時刻から 1 周期である」 |
| N16 | 判定へ停止のトークンを渡さない | T-79「停止要求は走っている判定へ届く」 |
