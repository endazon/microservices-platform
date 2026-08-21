---
title: IADR-0233 Wolverine 共通ヘルパはブローカ固有 API まで抱え、封じ込めは「他所で書けない」と「本拠に在り続ける」の両方で検査する
type: impl-adr
status: Accepted
related_ids:
  - ADR-0027
  - ADR-0030
  - IADR-0217
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (§Wolverine 移行チェックリスト 手順 3〜6)
---

# IADR-0233 Wolverine 共通ヘルパの射程と、封じ込め検査の形

## 状況

計画 `ADR-0027` の移行チェックリストは、手順 3〜5 を**共通ヘルパで**適用し、手順 6 で
**個別サービスでの逸脱を静的検査で禁止する**ことを求めている。#455 Phase 0（U0〜U3）で
「部分移行を検出できる状態」を作り終えたので、本 IADR はその共通ヘルパを新設する際の
実装判断を記録する（作業は #455 U4）。

着手前に **API を実測した**（`WolverineFx` / `WolverineFx.RabbitMQ` 6.24.4 を復元し、リフレクションで確認）。
記憶や二次資料から書かない —— #889 で「8 手順を自分の過去メモから引いて誤った」実例がある。

| 手順 | 実測した API |
| --- | --- |
| 3 | `RabbitMqTransportExtensions.ListenToRabbitQueue(WolverineOptions, string, Action<RabbitMqQueue>)` |
| 4 | `Wolverine.IPolicies.DisableConventionalLocalRouting()` |
| 5 | `WolverineOptions.ServiceLocationPolicy`（enum `AllowedButWarn` / `AlwaysAllowed` / `NotAllowed`） |

🔴 **手順 5 の既定値は `NotAllowed` である。** 計画 ADR が「`internal` 実装型に依存するハンドラが
**最初のメッセージ受信時に**落ちるのを防ぐ」と書いた根拠が、既定値の実測で裏取りできた。

## 決定

### 決定 1: 共通ヘルパはブローカ固有パッケージ（`WolverineFx.RabbitMQ`）まで参照する

既存の `MassTransitExtensions` は「RabbitMQ 依存は各サービス側が持つため、ここはブローカ非依存の
core のみ参照する」という方針を採っており、**本決定はその前例から意図的に外れる**。

理由は手順 3 の性質にある。手順 3 の適用点は `ListenToRabbitQueue` という**ブローカ固有 API の上に
しか存在しない**。ブローカ非依存に留めると、共通ヘルパが提供できるのは「キュー名を組み立てる
純粋関数」までになり、**適用点そのものは各サービスに残る**。それでは手順 6 の「個別サービスでの
逸脱を静的検査で禁止する」が成立しない —— サービス側に `ListenToRabbitQueue(名前)` を書く自由が
残る限り、名前を前置しない書き方は常に可能である。

**封じ込めるべきは名前の作り方ではなく、適用点である。** よって `ListenToPlatformQueue` を
共通ヘルパへ置き、`WolverineFx.RabbitMQ` を参照する。

代償は、ブローカを増やすとき（`ADR-0027` は Kafka 併用を含む）に共通ヘルパが
`WolverineFx.Kafka` も抱えることである。**受容する** —— 抱えないと封じ込めが崩れる。

### 決定 2: 封じ込め検査は「他所で書けない」と「本拠に在り続ける」の**両方**を見る

`scripts/check-backend-libraries.js` の規則 5 は 2 つの半分を持つ。

- **(a)** 許可ファイルの外で封じ込め API が使われていたら fail
- **(b)** 🔴 **許可ファイル（本拠）から封じ込め API が消えていたら fail**

🔴 **(a) だけでは規則 5 は静かに no-op になる。** (a) の観点では「リポジトリのどこにも
書かれていない」状態が満点であり、共通ヘルパから手順 4 の 1 行を削っても検査は緑を返す。
封じ込めとは「1 箇所に集める」ことであって「どこにも無い」ことではない。

本リポジトリは**同型の fail-open を 2 度実測している** —— #883（owner の形を変えたとき合計が
`NaN` になり「0 件走査で緑を返さない門」が静かに開いた）と #889（自己試験を評価ループより後ろへ
置き、件数だけが増えて合否が一度も見られなかった）。いずれも**数字は動くのに門が開いていた**。
検査器を足す時点で (b) を置くのは、その 2 度から引いた規律である。

### 決定 3: 許可はプロジェクト単位ではなく**ファイル単位**にする

本拠の試験ファイルは封じ込め API のシンボルを書く必要がある（手順 5 が
`AlwaysAllowed` であることを assert するため）。これを `*.Tests` プロジェクト単位で許すと、
**各サービスのテストが逸脱した配線を組めてしまう**。許可はファイルのフルパス 2 件に限る。

### 決定 4: 部分移行の安全弁の存在を、散文ではなくテストで固定する

`AddPlatformPipelineStep<TConsumer>` と `IntrospectionBuilder.AddStep<TConsumer>` の
`where TConsumer : class, IConsumer, IPipelineStep` は、**部分移行に対する現存する唯一の
コンパイル時安全弁**である（#455 Phase 0 の実測）。これは **U5 で意図的に外す**。

従前この着手順の拘束は**申し送りの散文にしか無かった**。散文は読まれないことがあるので、
`PartialMigrationSafetyValveTests` が制約の存在を assert する。U5 はこのテストを落とさずに
型制約を緩められない。**落ちること自体が設計意図**であり、U5 はテストを削除するのではなく
「安全弁は検査器側（`check-event-topology.js` のトランスポート認識判定）へ移った」ことを
示す形へ書き換える。

### 決定 5: 手順 4 の観測はリフレクションで行う

`DisableConventionalLocalRouting()` が変える状態は `WolverineOptions.LocalRoutingConventionDisabled`
（**internal**）にしか現れない（全インスタンスフィールドの前後差分を取って特定した。変化したのは
1 個だけ）。公開 API に観測点が無いため、テストはリフレクションで読む。

版更新で名前が変わったときに**静かに no-op 化しない**ことが要件である。`GetProperty` が `null` を
返したら例外を投げるため、観測点の消失はテストの失敗として現れる。

### 決定 6: 新設した試験プロジェクトへ MassTransit の `using` を持ち込まない

安全弁の制約に含まれる `IConsumer` は MassTransit の型である。素直に書くと
`using MassTransit;` が要り、**不採用ライブラリの残件が 13 → 14 へ増える**
（`check-backend-libraries.js` 規則 1 の ratchet が実際に検出した）。撤去対象の依存を、
撤去を守るための試験が増やすのは本末転倒である。

完全修飾名（`MassTransit.IConsumer`）で書けば同検査は `using` 行しか見ないので素通りするが、
**それは検査の回避であって遵守ではない**。よって型を取らず、制約の**型名（`FullName`）で照合する**。
リポジトリ内の型（`IPipelineStep`）は `typeof(...).FullName` で書き、改名をコンパイラに捕まえさせる。

## 影響

- `Platform.Shared.Infrastructure` が `WolverineFx` / `WolverineFx.RabbitMQ` を参照する。
  `src/Directory.Packages.props` の「この時点ではまだどの .csproj からも参照されていない」は
  **本作業で偽になる**ため是正した（規則 10）。`WolverineFx.RuntimeCompilation` は手順 2 の射程で
  あり、ホストを起こす各サービスが足す —— [IADR-0217](IADR-0217_wolverine-runtime-compilation-standard.md)
  決定 4 は引き続き成り立つ。
- `Platform.Shared.Infrastructure.Tests` を新設した。同プロジェクトは従前テストを持たず、
  `PipelineExtensions` などは knowledge 側のサービステストから間接的に試験されていた。
- **本 PR は既存の MassTransit 経路を 1 行も変えない。** 5 コンシューマの移し替えは別 PR である。

## 代替案

- **共通ヘルパをブローカ非依存に留める**（決定 1 の否）: 前例には忠実だが、手順 3 の適用点が
  サービス側に残り手順 6 が成立しない。**封じ込めの成立を優先した。**
- **規則 5 を (a) だけにする**（決定 2 の否）: 実装が最も小さいが、本拠から消えたときに
  静かに緑を返す。過去 2 度の fail-open と同型であり、採らない。
- **安全弁を U4 で外してしまう**（決定 4 の否）: U5 の作業が減るが、トランスポート認識検査
  （#883）以外の防壁が無い期間を作る。**着手順の拘束（U2 → U5）に反する。**

## フォローアップ

- **U5**（型制約の緩和）で `PartialMigrationSafetyValveTests` を書き換える。その際、
  `check-event-topology.js` のトランスポート認識判定が実際に部分移行を捕まえることを
  変異試験で確かめてから外すこと。
- ブローカを Kafka へ広げるとき、決定 1 の代償（共通ヘルパが `WolverineFx.Kafka` も抱える）が
  現実になる。抱える形が辛くなったら、封じ込めを「参照の集中」から「Roslyn アナライザ」へ
  移すことを検討する（現時点では過剰）。
