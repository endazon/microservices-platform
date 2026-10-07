---
title: 作業仕様書 — enabled:false の Wolverine 段で受信キューを宣言・束縛せずリスナーも立てない（#1801）
type: spec
status: done
related_ids: [FR-14, FR-15, ADR-0018, ADR-0027, IADR-0028, IADR-0239, IADR-0314]
author: claude
created: 2026-10-08
updated: 2026-10-08
issue: "#1801"
---

# 作業仕様書 — enabled:false の Wolverine 段で受信キューを作らない（#1801）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。基点は MSP `origin/develop` `8505edb0`。
> 計画は project-planning `b5b584f`（隣接クローン・読み取り専用）。
> 裁定済み: issue の「決めること」は **選択肢 1（実装を寄せる）** を採る（利用者裁定。再確認しない）。

## 起点（トレーサビリティ）

- FR-14（コンポーザビリティ。宣言的パイプライン構成）。FR-15（自己申告）は挙動を変えないため関連として引く。
- 計画 ADR-0018（イベント接続を構成定義から生成する）・ADR-0027（Wolverine 採用・手順 3）。
- IADR-0028（`enabled: false` は購読・キュー非生成。本 issue はこの決定に実装を寄せる）・
  IADR-0239（`AddPlatformWolverineStep` の戻り値で queue 宣言を受ける）・IADR-0314（購読側の束ね `BindPlatformQueue`）。
  いずれも本文は凍結記録として書き換えない。
- #1799 / PR #1800 の実装エージェントが発見し、独立監査がコードで確認した所見。

### 受け入れ基準の文言の確認（計画）

計画 `02_requirements/01_requirements.md` の FR-14 受け入れ基準は「段構成の変更を、コア改修なしの構成定義変更のみで適用でき、
ロールバックできる」であり、`enabled` の挙動そのものは書かない。「`enabled: false` で購読・キューを生成しない」は
計画 `06_technical/10_composability-design.md`（スキーマの表現力は「段の有効/無効・キュー名上書き」）を受けて
**実装側 IADR-0028 が確定した規則**であり、`PipelineOptions.Enabled`・`pipeline.schema.json`・FR-14 試験仕様書が同じ文言を持つ。
本 issue はこの規則に実装を寄せる。計画への環流は不要である（計画の文言と食い違わない）。

## 受け入れ基準

- AC1: `enabled: false` の Wolverine 段について、受信キューを fan-out exchange へ束ねない（キューを宣言しない）。
- AC2: 同じく、受信キューのリスナーを立てない。
- AC3: `enabled: true`・宣言なし（null）の段は従来どおり束ね・リスナーを立てる。`queue` 上書きも従来どおり効く。
- AC4: 発行側の経路（`RoutePlatformEvent`）の挙動は変えない。
- AC5: AC1〜AC3 をテストで固定し、ガードを外す変異でテストが落ちることを確かめる。
- AC6: 母集合（下）の文書・コメントを新しい（真になった）挙動へ揃える。`docs/` は trace ブロックと `updated:` を守る。

## 設計

### 形の選択: 段宣言を受ける共通ヘルパの多重定義（各 `Program.cs` のガードではなく）

`WolverineExtensions` に、段宣言（`PipelineStepOptions?`）を受ける多重定義を足す。

- `RabbitMqTransportExpression.BindPlatformQueue<TEvent>(string serviceName, PipelineStepOptions? step)`
- `WolverineOptions.ListenToPlatformQueue<TEvent>(string serviceName, PipelineStepOptions? step)`（無効なら null を返す）
- `PlatformStepQueueName<TEvent>(PipelineStepOptions? step)` —— `step?.Queue ?? typeof(TEvent).Name`（従前各 `Program.cs` に
  複写されていた式の単一情報源）

`step is { Enabled: false }` のときは何もしない。`step` が null（宣言なし＝規則 1 の既定登録）と `Enabled: true` は従来どおり。

採った理由:

1. **ガードと既定キュー名を 1 箇所に置き、そこをテストで固定できる。** 各 `Program.cs` に `if` を 5 箇所（7 段）書く形は、
   `Program.cs` を単体で起こすテストが無いため、ガードの 1 つが消えても検出できない。
2. **ガイド §2.1 の手順が 1 呼び出しのまま**（`step` をそのまま渡す）で、新しい段の作者がガードを書き忘れる余地が無い。
3. **名前は `BindPlatformQueue<TEvent>` のまま**にする。`scripts/check-event-topology.js` の `findBoundEvents` は
   購読側の束ねを `BindPlatformQueue<Ev>` の字面で検出する（`:257-259`）。名前を変えると検査器も直す必要が生じ、射程が広がる。
4. 文字列を受ける既存の多重定義は残す（統合テストの器・既存テストが使う。#1796 の担当範囲の統合テストには触れない）。

不採用: `AddPlatformWolverineStep` が Bind/Listen まで引き受ける形。同メソッドは `WolverineOptions` しか受けず、
束ねに要る `RabbitMqTransportExpression`（`UseRabbitMq(...)` の戻り値。接続文字列は各サービスが解決する）を持たない。
受け口を広げると、Conversion の発行経路（同じ `UseRabbitMq` 式）と絡み、変更が大きくなる。

### IADR を起こさない理由

決定は IADR-0028 が既に持つ（「`enabled: false` は購読・キュー非生成」）。本件はその決定へ実装を寄せる不具合の是正であり、
新たな設計判断（選択肢の比較を要するもの）は上の「形の選択」だけで、共通ヘルパへの多重定義の追加にとどまる。

### 射程外・残余

- **既に作られたキュー・束縛は消さない。** 有効だった段を後から無効にした場合、RabbitMQ に残る永続キューと exchange への束縛は
  本変更では削除されない（Wolverine の `AutoProvision` は宣言するだけで削除しない）。リスナーは立たないため、残った束縛に
  届いたメッセージはそのキューに溜まる。運用で削除する旨を Helm の README に書く。現在の `pipeline.json` は全段 `enabled: true`
  （`deploy/helm/microservices-platform/files/pipeline.json` の 8 段。実測）であり、配備済み環境に該当する段は無い。
- MassTransit の段（`DocumentService` の `DocumentNormalizedConsumer`）は対象外。`AddPlatformPipelineStep` は `enabled: false`
  で `AddConsumer` を呼ばず、`ConfigureEndpoints` はコンシューマの無いエンドポイントを作らないため、既に「キューを生成しない」
  （`PipelineExtensions.cs:122-128`）。
- 発行側（`RoutePlatformEvent`）は変えない（AC4）。Conversion は再試行で `RawDocumentFetched` を発行するため、段が無効でも
  発行経路は要る。
- 統合テスト（`src/knowledge/backend/Tests/Knowledge.IntegrationTests/`）の器は文字列の多重定義を使い続ける。#1796 と
  ファイル領域が重なるため触らない。

## 母集合（規則 9・10）

### コード: `AddPlatformWolverineStep` の後に `BindPlatformQueue` / `ListenToPlatformQueue` を呼ぶ `Program.cs`

`git grep -n "AddPlatformWolverineStep\|BindPlatformQueue\|ListenToPlatformQueue" -- '*.cs'` で引いた（issue の 2 件ではなく 5 件）。

| サービス | 段 | 箇所 |
| --- | --- | --- |
| ConversionService | `RawDocumentFetchedConsumer` | `Program.cs:170-186` |
| GraphService | `DocumentDeletedConsumer` / `GraphDocumentSyncConsumer` | `Program.cs:353-369` |
| IngestionService | `DocumentUpdatedConsumer` | `Program.cs:145-156` |
| RetrievalService | `DocumentDeletedConsumer` | `Program.cs:252-263` |
| WikiService | `DocumentDeletedConsumer` / `DocumentSyncConsumer` | `Program.cs:124-140` |

除外: 統合テストの器（`RawDocumentFetchedEdge.cs:142-150`・`WolverineBrokerEdge.cs:207`）—— 段は常に有効で組む試験の器であり、
#1796 と領域が重なる。共有基盤のテスト（`WolverineExtensionsTests.cs`・`PortSwapCompositionTests.cs`）—— 文字列版の試験として残す。
ai-stock-trading（`/home/user/ai-stock-trading`）は 3 つの API のいずれも使っていない（`grep -rln` で 0 件）。

### 文書・コメント: 挙動を述べる記述

`git grep -n -e "キューを生成しない" -e "キュー非生成" -e "キューは宣言され" -e "ハンドラを登録しない" -e "no subscription or queue" -e "購読・キュー" ...`
（`.ai-context/specs` / superpowers / `CHANGELOG.md` を除く）で引いた。

| 箇所 | 現状 | 対応 |
| --- | --- | --- |
| `WolverinePipelineExtensions.cs:145` 規則 8 のコメント・`:163-165` 警告ログ | 「購読・キューを生成しない」（Wolverine では偽） | ハンドラ除外は本メソッド、キューの抑止は段宣言版の Bind/Listen が担うと書き分ける。ログ文言も同様 |
| `WolverinePipelineExtensions.cs:43-46` 戻り値の説明 | 「呼び出し側が `ListenToPlatformQueue` へ渡す」 | 段宣言版へ渡すと書く |
| `pipeline.schema.json:32` description | 「enabled=false で購読・キューを生成しない」 | 本変更で真になる。**文言は変えない** |
| `PipelineOptions.cs:54` コメント | 同上 | 真になる。変えない |
| `PipelineExtensions.cs:73` / `:127`（MassTransit） | 同上 | 元から真。変えない |
| `deploy/helm/microservices-platform/files/README.md:32-33` / `:38-39` | 「キューの宣言・束縛は現状まだ行われる」・旧手順 | 新しい挙動・既存キューは消えない旨・段宣言版の手順へ |
| `docs/tests/FR-14_composability.md:36` | 「受信キューは現状まだ宣言される」 | 「ハンドラ・受信キューの束縛・リスナーのいずれも作らない」へ |
| `docs/functional/FR-14_composability.md:52` | 「受信キューは戻り値の `queue` 宣言で … 張る」 | 段宣言版が張り、無効の段では張らないと書く |
| `docs/tech/composable-component-guide.md` §2.1 手順 3・制約 | `var queue = step?.Queue ?? nameof(TIn)` を経由する手順 | 段宣言版の 2 呼び出しへ。制約の行に「キューも作らない」を足す |
| `ConversionService/Tests/.../PipelineStepRegistrationTests.cs:133` | 規則 8 を「購読・キューを生成しない」と説明 | 同テストが測るのはハンドラ登録だけなので「ハンドラを登録しない」へ絞り、キューは共有基盤の試験が固定すると書く |
| `WikiService/Tests/.../PipelineRecomposeTests.cs:121` | 「登録しない」 | 真。変えない |
| `IntegrationTestFactory.cs:141` | MassTransit の規則 5 の説明 | 真。統合テストでもあるため触らない |
| `scripts/validate-pipeline-config.js:97` / `:111` | 「無効の段は購読しない」 | 本変更で Wolverine でも真になる。変えない |
| `docs/screens/SC-11_configuration-viewer.md:114`・IADR-0036 / IADR-0268 | 表示・ドリフトの扱い | キューの有無を述べない。対象外 |
| IADR-0028 `:62` | 「`enabled: false` は購読・キュー非生成」 | 凍結記録。本変更で真になる |

規則 10（この変更で新たに誤りになる自分の記述）: 各 `Program.cs` の「queue 宣言があればそれを、無ければイベント型名を使う」の
コメントは、その式を共通ヘルパへ移すため、ヘルパの側を指すように直す。#1800 の作業仕様書・PR 本文の「キューの有無は主張しない」は
凍結記録として残す。

## テスト

`Platform.Shared.Infrastructure.Tests/Foundation/Extensions/WolverineExtensionsTests.cs` に追加する（ブローカ不要。
`options.Transports` の rabbitmq エンドポイントを公開 API で観測する既存の形）。

- 段宣言が `enabled: false` → 束ねたキュー（`<service>.<event>`）のエンドポイントが無い・exchange の束縛が無い。
- 段宣言が `enabled: false` → リスナーが立たない（戻り値 null・キューのエンドポイントが無い）。
- 段宣言が `enabled: true`（`queue` 上書きあり）→ 上書き名で束ね、リスナーが立つ。
- 段宣言が null → イベント型名で束ね、リスナーが立つ。

変異確認: 両多重定義のガードを外して該当テストが落ちることを確認する（結果は PR 本文に記す）。

## 再配備

`Program.cs` を変える 5 サービス（conversion / graph / ingestion / retrieval / wiki）はイメージの再ビルドが要る。
現在の `pipeline.json` は全段有効であり、配備済み環境での挙動は変わらない。
