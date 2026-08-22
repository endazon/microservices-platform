---
title: IADR-0234 #455 はここで成長を止め、baseline がゼロになることで測られるものはすべて #441 が持つ。移行の単位はイベント辺であり、型制約は緩和ではなく C3 で始末する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0027
  - ADR-0030
  - IADR-0116
  - IADR-0137
  - IADR-0217
  - IADR-0233
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md (§決定・再試行は Wolverine の耐久メッセージ機能で賄う)
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (§Wolverine 移行チェックリスト = 8 手順の原典・§リスク・未決事項)
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md
---

# IADR-0234 #455 と #441 の境界を確定し、移行の単位をイベント辺に置く

## 状況

### 1. 「誰が baseline を空にするのか」に、3 つの食い違う答えが live で残っている

🔴 **当初の見立て（「マスタ仕様書と baseline JSON が違うことを書いている」）は誤りである。**
両者は **同じこと** をほぼ逐語で書いている。実際の食い違いは 3 者の間にある。

| # | 出典 | 書いてあること |
| --- | --- | --- |
| A | `.ai-context/specs/20260803_issue-455_backend-application-standard.md:75-76`（#455 マスタ仕様書 §対象外） | 「サービス間通信（ブローカー・トランスポート・gRPC/REST 境界）— **#441** の担当。本作業は各サービス *内部* のアプリケーション層に限る」 |
| B | 同 `:145` ＋ `scripts/backend-library-baseline.json:5` ＋ `scripts/check-backend-libraries.js:24`・`:1225`・`:1245` | 「**各サービスの再実装 issue（#438〜#451）** は移行と同時に baseline から自プロジェクトを削除する」 |
| C | 着地した実績（`74b0f99` #883 / `17e3a22` #889 / `071e356` #897 ほか。仕様書の `issue:` も一貫して #455） | Wolverine のトポロジ検査・禁止 API・共通ヘルパはすべて **#455** で着地している |

A は「トランスポートは #441」と言い、B は「baseline 行の削除（＝ MassTransit 撤去＝トランスポート移行）は
#438〜#451」と言い、C は実際には #455 でやっている。**3 者は両立しない。**

さらに B は **データと噛み合っていない**。baseline の 13 行はすべて `MassTransit` であり、
「サービスごとに独立した自分の行」ではなく、**1 つの横断的なメッセージング移行の断面** である。

🔴 **B の文言は `scripts/check-backend-libraries.js:1225` の `writeBaseline()` が逐語で再生成する。**
JSON だけを直しても、次に `--write-baseline` を実行した誰かが **黙って元へ戻す**。

### 2. サービス単位の「切替」は CI が通らない —— ただし「移行が不可能」ではない

U2（#883）が `scripts/check-event-topology.js` をトランスポート認識にした。
`transportMismatches()`（`:310-334`）は **イベント × 購読者ごと** に、発行側と購読側がトランスポートを
1 つも共有しなければ違反とする。実測（本リポジトリのソースを鏡像へ複製して変異させた）:

```text
=== migrate ONLY knowledge/DataSourceService  -> violations=1
=== migrate ONLY knowledge/ConversionService  -> violations=2
=== migrate ONLY knowledge/DocumentService    -> violations=4
=== migrate ONLY knowledge/IngestionService   -> violations=1
=== migrate ONLY knowledge/WikiService        -> violations=2
--- 1 イベント（辺）単位で切り替えた場合 ---
only [RawDocumentFetched] -> violations=0   only [DocumentNormalized] -> violations=0
only [DocumentDeleted]    -> violations=0   only [DocumentUpdated]    -> violations=0
```

🔴 **ただし「サービス単位の移行は不可能」は言い過ぎであり、採らない。**
同検査は **二重購読（MT と Wolverine の両方で待つ）を意図的に違反にしない**
（`scripts/check-event-topology.js:44-46`「交差が空でないためである」・`:303-305`「これが『切替』を
『追加』に分解して 1 PR を小さく保つための前提になる」）。実測でも、WikiService だけで
`IConsumer<T>` を残したまま Wolverine ハンドラを足した鏡像は **violations=0 / EXIT=0** であった。

正しい言い方は次である。**CI が緑になる最小単位は、サービス単位の「二重購読の追加」まで小さくできる。
しかし baseline の行が減る（＝ MassTransit を落とし切る）最小単位はイベント辺である。**
そして辺は必ず 2 サービス以上にまたがる。

| イベント | 発行 | 購読 |
| --- | --- | --- |
| `RawDocumentFetched` | DataSourceService（＋ **ConversionService**。下記 4 参照） | ConversionService |
| `DocumentNormalized` | ConversionService | DocumentService |
| `DocumentUpdated` | DocumentService | IngestionService **と** WikiService |
| `DocumentDeleted` | DocumentService | WikiService |

### 3. U5 は「緩和」として記録されているが、緩和に相当する中間形が（片方には）無い

安全弁は 2 箇所の `where TConsumer : class, IConsumer, IPipelineStep` である
（`PipelineExtensions.cs:77` / `IntrospectionExtensions.cs:63`）。呼び出し側は実測で
`AddPlatformPipelineStep` 8 件・`AddStep` 5 件。

- **`AddPlatformPipelineStep`**: レシーバ自体が MassTransit 型（`this IBusRegistrationConfigurator bus`）で、
  本体は `bus.AddConsumer<TConsumer>()` を呼ぶ。**型制約だけを緩めても意味を持つ中間形が無い** どころか、
  **メソッドごと MassTransit 専用** である。Wolverine 版は導出ではなく新規に書くことになる。
- 🔴 **`IntrospectionBuilder.AddStep` は違う。** レシーバは自リポジトリの型であり、イントロスペクションは
  トランスポート非依存の基盤機能（FR-15 / ADR-0018）である。`where TConsumer : class, IPipelineStep` は
  **コンパイルし、かつ移行後も意味を持つ**（宣言 `pipeline.json` の `steps[].name` と実装の結び付きを保つ）。
  ただし `input` の導出が `IConsumer<>` のリフレクションに依存しており、制約だけ外すと
  `input` が黙って `string.Empty` になる（`IntrospectionExtensions.cs:66-69`）。

### 4. 検査器は安全弁の完全な代替ではない（3 つの穴を実測した）

- `ConversionJobEndpoints.cs:70` の `bus.Publish(ev, ct)` は `RawDocumentFetched`（再変換の再投入）だが、
  検査器からは **見えない**。baseline はこのイベントの発行元を DataSourceService だけと記録している。
  辺 `RawDocumentFetched` を「正しく」移行した鏡像は **EXIT=0（緑）のまま、再変換経路が死ぬ**。
- `TagDictionaryEndpoints.cs:136` の `DocumentUpdated` 発行も同様に見えない。
- `transportMismatches()` は **発行側のトランスポートを和集合で取る**（`:317-318`）。MT 発行元が 1 つでも
  残っていれば、MT 購読側の食い違いは **すべて隠れる**。

型制約は登録点に対して **全域** だった。検査器は **見える発行だけを覆う部分的な網** である。

## 決定

### 決定 1: #455 は U4 で成長を止める。baseline がゼロになることで完了が測られるものはすべて #441 が持つ

- **#455 が持つもの**: いま何が運んでいるかと無関係に **標準を定義・強制** するもの ——
  検査器・ratchet・雛形・文書、および着地済みの U0〜U4。
- **#441 が持つもの**: 本番メッセージを MassTransit から Wolverine へ **動かす** 変更のすべて。
  W1〜W3・E1〜E3b・C1〜C3（決定 3）はすべて #441 に属する。
- **本 IADR の着地をもって #455 はクローズしてよい。** 受け入れ基準（同仕様書 `:167-169`）は
  「検査器が成功する」であり、baseline がゼロになることを条件にしていない。

これは A（マスタ仕様書 §対象外）を正とし、B を誤りとして棄却する裁定である。

### 決定 2: 「各サービスの再実装 issue が自プロジェクトの行を削除する」は誤りであり、5 箇所すべてを訂正する

理由は状況 2 のとおり —— **行はサービス単位では落ちない。**
`scripts/backend-library-baseline.json` の `$comment` と、それを再生成する
`scripts/check-backend-libraries.js:1225`、頭注 `:24`、実行時 notice `:1245` を同一 PR で訂正する
（**JSON だけ直すと `--write-baseline` が戻す**）。マスタ仕様書 `:145` は凍結記録のため
本文を書き換えず、日付つき追記で本 IADR を指す。

### 決定 3: 移行の単位はイベント辺とし、チェーンを W1〜C3 として定義する

🔴 **「W1」「C3」等の記号は本 IADR で初めて定義する**（従前どこにも定義が無かった）。

| 単位 | 内容 | 完了後の baseline |
| --- | --- | --- |
| **W1** | Wolverine 共通ヘルパへ retry/DLQ 既定＋等価性試験（本 PR） | 13 |
| W2 | `pipeline.json` 突合 fail-fast の Wolverine 版登録経路 | 13 |
| W3 | 手順 8 の実ブローカ結合テスト基盤（Testcontainers.RabbitMq） | 13 |
| E1 | 辺 `RawDocumentFetched` | 11 |
| E2 | 辺 `DocumentNormalized` | 9 |
| E3a | 辺 `DocumentDeleted` | 9 |
| E3b | 辺 `DocumentUpdated` fan-out | 3 |
| C1 | `Knowledge.Contracts.Tests` から MT 依存撤去 | 2 |
| C2 | `Knowledge.IntegrationTests` の MT ハーネス撤去 | 1 |
| C3 | MT 経路撤去・型制約の始末・安全弁テスト書き換え・baseline 空化・CPM から MassTransit 削除 | 0 |

**辺はサービスをまたぐため、#438〜#451（サービス単位の再実装 issue）には入れられない。**
二重購読の追加だけならサービス単位で緑にできるが、それでは行が減らない。

**残る 3 行はどの辺からも到達しない**（`Knowledge.Contracts.Tests` / `Knowledge.IntegrationTests` /
`Platform.Shared.Infrastructure`）。C1〜C3 を別建てにしているのはそのためである。

> 🔴 ［2026-08-22 追記 / #441］**チェーンへ `W4`（Wolverine ブローカ readiness）を足した。**
> E1 の着手前に実測して判明した —— `AddMassTransit` が暗黙に登録していた
> `masstransit-bus`（tag `ready`）に対し、**`AddWolverine` ＋ `UseRabbitMq` はヘルスチェックを
> 0 件しか登録しない**。気づかずに発行元を移すと `/health/ready` がブローカ不達でも 200 を返し、
> **probe が自分の主張することを検査しなくなる**。同じ readiness コメントは DataSourceService・
> DocumentService・WikiService の 3 つに在り、E1〜E3b へ等しく効くため辺の移行とは別単位にした。
>
> **位置は W3 の次・E1 の前**（前提は W1 のみ）。**baseline は 13 のまま動かさない。**
> 実装は共通ヘルパへの **opt-in の別拡張**とする —— 自動登録にすると
> `AddPlatformHealthChecks` の本番 call site **12 箇所すべてが Wolverine を配線していない**（実測）ため、
> **メッセージングと無関係な 12 サービスがブローカ停止で 503 を返す**。
> 「壊れているのに 200」の逆で「無関係なサービスが騒ぐ」形だが、どちらも readiness の意味を壊す。
>
> **W4 の実測（2026-08-22。クラスタの RabbitMQ へ `kubectl port-forward` し、自前の TCP 中継を挟んで測った。ブローカには触れていない）:**
>
> 1. 🔴 **起動時のブローカ障害は readiness の対象外である。** 到達不能なブローカに対し Wolverine ホストは
>    **20 回再試行し、約 135 秒後に `BrokerInitializationException` で起動に失敗する**。
>    ホストが立たない以上 `/health/ready` は存在せず、**試験対象が無い**（pod は crash loop になる＝安全側）。
>    **readiness が守れるのは「起動後に到達できなくなる」場合だけ**である。
> 2. **ブローカ障害は degrade ではない。** 上のとおり**起動を約 135 秒ブロックしてから失敗**させる。
>    E1 以降のデプロイ窓の考慮事項であり、従前どこにも記録が無かった。
> 3. **検知はキャッシュされない。約 3 秒である。** 3 秒間隔で観測した実測値:
>    `t=45s Healthy` →（中継を遮断）→ `t=48s Unhealthy Msg=RabbitMQ sending connection is down`。
>    これが「W4 は装飾ではない」ことの根拠である。
> 4. 🔴 **`BuildHealthCheck` の shadowing の罠。** `ITransport.BuildHealthCheck` は既定実装つきの
>    **virtual** メソッドだが、`RabbitMqTransport` 側の同名メソッドは **non-virtual**（override ではなく
>    shadow）である。よって**インタフェース型で呼ぶと null が返る**。これを踏むと
>    **健全なブローカに対して恒久的に Unhealthy**（readiness が永久に赤い）になる。実際に一度書いて踏んだ。
>    具象型に宣言されたメソッドを名指しで呼ぶこと。**「取得できない」は Healthy ではなく Unhealthy に倒す** ——
>    観測できないことは、異常が無いことの証拠ではない。
>
> ⚠️ **限界。** 再現に使ったのは自前の TCP 中継の切断であり、**ブローカのクラッシュとバイト等価ではない**
> （RST / 無応答 / 半開の違いがある）。再現できたのは「確立済み接続が落ち、再接続もできない」形である。

### 決定 4: U5 は「型制約の緩和」としては発生しない。IADR-0233 決定 4 をここで改める

[IADR-0233](./IADR-0233_wolverine-shared-helper-confinement.md) 決定 4（Superseded by 本 IADR 決定 4）は
安全弁を「**U5 で意図的に外す**」と書いた。**U5 という単位は起こさない。** 代わりに:

- **`AddPlatformPipelineStep` は C3 でメソッドごと削除する**（緩和ではない。状況 3）。
- **`IntrospectionBuilder.AddStep` は緩和ではなく「置き換え」である。** 制約を
  `where TConsumer : class, IPipelineStep` へ狭めるのと同時に、`input` の導出を
  `IConsumer<>` のリフレクションから **トランスポート非依存の情報源へ移す**
  （`IPipelineStep` へ入力型を宣言させる等）。**制約だけ外して `input` を空にする改変は禁じる** ——
  検査が通ったまま宣言突合が空洞化する。

**W1（本 PR）では条件を明記するだけで、削除も緩和も行わない。**

### 決定 5: C3 が安全弁を外す前に要る証跡

1. E3b・C1・C2 のマージ。
2. 両制約メソッドの呼び出しが 0 件であることの grep（実測。現在は 8 件 / 5 件）。
3. `check-event-topology.js` が部分移行を検出することを示す **変異試験** —— 実ソースの
   `DocumentSyncConsumer` を Wolverine 記法へ変え、発行側を MT に残して **EXIT=1** を実測する。
   併せて **二重購読が緑のままである負の対照** も取る（緑にならないと決定 3 の分解が壊れる）。
4. 🔴 **状況 4 の 3 つの穴を証跡へ明記する。** 「検査器が安全弁と等価である」とは書かない。
   等価ではない —— 型制約は全域、検査器は見える発行だけを覆う部分的な網である。

### 決定 6: retry/DLQ の等価性は「挙動」で固定し、測れないものは測れないと書く

W1 が固定するのは次である（`WolverineExtensionsTests`）。観測点は Wolverine が実行時に実際に引く
`FailureRuleCollection.DetermineExecutionContinuation` そのものである。
**「どちらも例外を投げる」程度の粗い assert では、回数も間隔もデッドレター送りの条件も 1 つも固定できない。**

| 項目 | MassTransit | Wolverine（本 PR） |
| --- | --- | --- |
| 適用前の既定 | —— | **再試行 0 回・初回失敗で即デッドレター**（実測） |
| 再試行回数 | `RetryIntervals.Length` = 3 | 同 3（試行 1〜3 が再試行） |
| 間隔 | 2s / 10s / 30s | 同（判定関数から読んで突合） |
| 試行上限 | `MaxAttempts` = 4 | 同 4 |
| デッドレター送りの条件 | 再試行を使い切った失敗 | 試行 4 回目で `MoveToErrorQueue`（3 回目はまだ再試行） |
| 例外の範囲 | 型で絞らない | `OnAnyException`（3 種の例外型で実測） |

🔴 **両側の数値は独立に置く。** 共有すると「両方を同時に変える変異」が素通りし、
**変異が当たらない試験** になる。一致は試験が束ねる（[IADR-0137](./IADR-0137_conversion-dead-letter-marker.md) 決定 3 の先例）。

🔴 **測れないものを測ったことにしない。** MassTransit はエンドポイントごとの `<queue>_error` へ送るが、
Wolverine の RabbitMQ 既定は **単一の共有 `wolverine-dead-letter-queue`** である（実測）。
これは監視・再投入の運用が見るキューが変わる **実在の差** である。
`RabbitMqQueue.DeadLetterQueue` / `RabbitMqListenerConfiguration.DeadLetterQueueing` が
キューごとの指定点だが、**構成時点の観測では名前が変わらず、単体テストでは固定できない**（実測）。
**デッドレターの宛先トポロジは W3（実ブローカ結合テスト）の射程とし、本 PR は等価と主張しない。**

## 結果

- #455 はクローズ可能になる。#441 が W1〜C3 を持つ。
- baseline の残件数がそのまま #441 の進捗指標になる（13 → 0）。
- 🔴 **計画側との差異が 1 件残る。** planning の 12_backend-application-stack §リスク・未決事項は
  移行の段取りを「**サービス単位の段階移行**」と書いているが、行が減る単位は辺である。
  **本リポジトリの実装判断では覆せないため、計画への環流（issue 起票）を要する。**
- IADR-0233 決定 4 は本 IADR 決定 4 に置き換わる。`PartialMigrationSafetyValveTests` の
  「U5 で外す」旨の記述と `WolverineExtensions.cs` の同旨のコメントは C3 の語へ改める。

## 関連

- [IADR-0233](./IADR-0233_wolverine-shared-helper-confinement.md)（決定 4 を本 IADR 決定 4 が改める）
- [IADR-0217](./IADR-0217_wolverine-runtime-compilation-standard.md)（`#438〜#451 / #441` の表記はここで #441 へ寄せる）
- [IADR-0137](./IADR-0137_conversion-dead-letter-marker.md)（数値を 2 か所に置いて試験で束ねる先例）
- [IADR-0116](./IADR-0116_reimplementation-branching-and-pr-policy.md)（1 issue = 1 PR）
