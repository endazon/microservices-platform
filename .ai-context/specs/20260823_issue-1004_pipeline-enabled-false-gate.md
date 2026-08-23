---
title: 作業仕様書 — enabled:false が規約探索を含めて段を止めることの追試（共通ヘルパ直接試験）
type: spec
status: draft
related_ids:
  - FR-14
  - NFR
  - ADR-0018
  - ADR-0027
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - "ADR-0027（メッセージング基盤 = Wolverine）"
related_adrs:
  - IADR-0233
  - IADR-0234
issue: "#1004"
---

# 作業仕様書: `enabled:false` が段を止めることを外形から確かめる（共通ヘルパ直接試験）

## 起点

`#1004`。`#998`（`#441` E1）で見つけて直した本番欠陥（`pipeline.json` の `enabled:false` が
Wolverine の規約探索〔conventional discovery〕には効いていなかった）の追試。修正そのものは
`WolverinePipelineExtensions.AddPlatformWolverineStep` 規則 8 に既に入っている
（`options.Discovery.CustomizeHandlerDiscovery(q => q.Excludes.WithCondition(...))`）。
本 issue が扱うのは「直っていない箇所」ではなく「**共通ヘルパ側の直接試験が無い**」という欠落である。

参照した実測の記録: `.ai-context/specs/20260822_issue-441_edge-rawdocumentfetched.md`
「`enabled:false` は効いていなかった」節（609〜653 行）。

## 欠けていた検証の実測

`Platform.Shared.Infrastructure.Tests/Foundation/Pipeline/WolverinePipelineExtensionsTests.cs` の
既存試験（`規則8_無効化した段は登録されない`）を読んだ。

```csharp
var step = options.AddPlatformWolverineStep<GoodStep>(Declared(enabled: false));
step.Should().NotBeNull();
RegisteredTypes(options).Should().NotContain(typeof(GoodStep));
```

`RegisteredTypes` は `HandlerDiscovery.ExplicitTypes`（＝ `IncludeType` で明示登録した型の一覧）だけを読む。
**これは「`IncludeType` を呼ばなかったこと」の確認であり、「規約探索が拾わないこと」の確認ではない。**
`#441` E1 の本番欠陥はまさにこの 2 つの違いから生じた —— `IncludeType` を呼ばなくても、規約探索は
`ExplicitTypes` とは独立に `HandlerQuery.Find(Assemblies)` でアセンブリを走査するため、明示登録の有無に
関わらず購読が生え得る。

さらに既存の `GoodStep` / `NoInputTypeStep` / `NoHandlerMethodStep` はいずれも型名が `Handler` / `Consumer`
で終わらず、`Saga` も `IWolverineHandler` も実装しない。Wolverine の規約探索の既定条件
（`HandlerQuery.Includes.WithNameSuffix("Handler"|"Consumer")` 等。実装は decompile で確認——後述）は
**名前サフィックスで判定する**ため、これらの型はそもそも規約探索の対象にならない。つまり既存試験群は
「規約探索が効いている条件」を一度も作っていない。

**結論: 共通ヘルパには「規約探索が実際に対象を拾う条件で、`enabled:false` がその対象を除外すること」を
測る試験が 1 件も無かった。** 本仕様書はこれを埋める。

### Wolverine 内部の観測点（decompile で確認・実装ではなく事実確認）

`ilspycmd` で `WolverineFx 6.24.4` の `Wolverine.Configuration.HandlerDiscovery` を decompile し、
以下を確認した（`/root/.nuget/packages/wolverinefx/6.24.4/lib/net10.0/Wolverine.dll`）。

- `internal (Type, MethodInfo)[] FindCalls(WolverineOptions options)` が実際の登録候補を計算する内部メソッドである。
  `_conventionalDiscoveryDisabled` が false（既定）なら
  `specifyConventionalHandlerDiscovery()`（`HandlerQuery.Includes.WithNameSuffix("Handler"|"Consumer")` 等を設定）
  を呼んだうえで `HandlerQuery.Find(Assemblies).Concat(_explicitTypes).Distinct()` を返す。
  **`ExplicitTypes`（＝ `IncludeType`）と規約探索（`HandlerQuery.Find`）は独立した経路であり、`FindCalls` の
  戻り値だけが両方を合成した「実際に登録される型」である。**
- `HandlerQuery`（`JasperFx.Core.TypeScanning.TypeQuery`）の `Find` は
  `assembly.FindTypes(Concretes|Closed) を Includes/Excludes でフィルタする`。**名前サフィックス
  `Handler` / `Consumer` の型は既定で `Includes` に合致する。** 本リポジトリの `pipeline.json` 段の実装
  クラスが軒並み `*Consumer` と命名されているのは偶然ではなく、これが `#441` E1 の本番欠陥の直接の原因である。
- したがって、共通ヘルパの直接試験でも**ダミー段の型名を `Consumer` で終わらせる**ことで、実際の生産コードと
  同じ経路（規約探索の名前サフィックス一致）を再現できる。ホスト（`IHost` / `UseWolverine`）を実際に起こさず
  `HandlerDiscovery.FindCalls` を直接呼べば、`ConversionService.Worker.Tests` の `PipelineStepRegistrationTests`
  が行っているホスト起動＋`InvokeAsync` より軽い形で同じ観測点を突ける（`FindCalls` はホスト起動時に内部的に
  呼ばれる計算そのものであり、host を経由しても経由しなくても同じ関数を呼ぶ）。

## 母集合の確認（着手前・トレーサビリティ規約の規則 9・10）

**引いた問い**: 「登録しなかった（明示登録なし）」＝「無効になった」という前提が成り立たない箇所が、
Wolverine の `enabled:false` 以外にも本リポジトリに無いか。

**走査方法**（`.claude/rules/traceability.md` 母集合規則 1・2・3・4 準拠。誤りの側〔＝「明示登録とは独立の
自動探索 API」〕の語で全文走査し、拡張子・行フィルタで絞らない）:

```bash
grep -rn "AddConsumersFromNamespaceContaining\|AddConsumers(\|Registration.AddConsumers\|ScanCurrentAssembly" src/
grep -rn "AddValidatorsFromAssembly\|services\.Scan(\|AddMediatR\|AddAutoMapper" src/
grep -rn "\.Enabled\b" src/ --include=*.cs -l
```

**結果**:

1. MassTransit 側の自動走査 API（`AddConsumersFromNamespaceContaining` / `AddConsumers()` /
   `ScanCurrentAssembly` 等）は**リポジトリ全体で 0 件**。`PipelineExtensions.cs`（MassTransit 経路）は
   `bus.AddConsumer<TConsumer>()` の明示登録のみを呼び、`enabled:false` のときはこの呼び出し自体を
   スキップする。**MassTransit には Wolverine の `HandlerQuery` に相当する「明示登録と独立にアセンブリを
   走査する既定動作」が無い**（オプトインの `AddConsumersFromNamespaceContaining` 等を使わない限り自動探索は
   発生しない）。ゆえに **MassTransit 側に同型の欠陥は無い**（コード読みでの確認。実ブローカでの追試は
   下記「MassTransit 版の扱い」節）。
2. `services.Scan(` / `AddMediatR` / `AddAutoMapper` / `AddValidatorsFromAssembly` の**アセンブリ走査 API は
   リポジトリ全体で 0 件**。したがって Wolverine 以外に「設定を切っても自動探索が生き残る」構造を持つ
   コンポーネントは無い。
3. `.Enabled` を持つ設定は 17 ファイルに分布するが、`DataSourceSyncOptions.Enabled`・`EmbeddingRouter` /
   `LlmRouter` の `.Enabled` はいずれも**単純な `if` 分岐**（`BackgroundService` の起動ガード・ルーティング
   候補のフィルタ）であり、Wolverine のような「独立した自動探索機構と併存する」構造を持たない
   （`.ai-context/specs/20260822_issue-441_edge-rawdocumentfetched.md` 「`enabled:false`（`pipeline.json`）は
   使えない」節で `sources[]` 側〔発行側〕には `enabled` 自体が無いことも既に実測・記録済み）。

**結論: 該当なし。** 同型の穴（「明示登録しない」＝「無効」という前提が、独立した自動探索によって
覆るケース）は Wolverine の `pipeline.json` 段登録以外に見つからなかった。

## MassTransit 版の扱い（受け入れ基準 2）

**「実ブローカで確かめない」と決定する。根拠:**

1. 本作業環境に Docker デーモンが無い（`docker info` が
   `dial unix /var/run/docker.sock: connect: no such file or directory` で失敗する。実測）。
   `docs/tests/TEST_STRATEGY.md` の統合テスト層（Testcontainers 前提）はこの環境では実行できない。
2. 上記「母集合の確認」でコード読みにより **MassTransit には自動アセンブリ走査 API の呼び出しが
   0 件であること**を確認済みである。「オプトインの走査 API を使っていない」という事実は静的に検証可能で
   あり、動的な確認（実ブローカ）が明らかにする余地は小さい —— Wolverine の場合と異なり、MassTransit は
   走査系 API を**呼ばない限り**動作しないため、「呼んでいないこと」の確認で十分である。
3. 一方で issue が指摘するとおり「将来誰かが `AddConsumersFromNamespaceContaining` 等を足せば同じ穴が
   開く」リスクは残る。これは**実ブローカでの動的確認では防げない**（足された時点でテストを書き直す必要が
   あるため）。恒久的な備えは、`scripts/check-backend-libraries.js` のような静的検査でこれらの API 呼び出し
   自体を ratchet 対象にすることだが、**「同型の事故が 2 回起きたら検査器を足す」**という運用ガイドの方針
   （本 issue の背景は 1 件目）に従い、**今回は検査器を新設しない**。将来 MassTransit 側にも走査系 API が
   混入したら、そのときに本件と合わせて 2 件目として検査器化を検討する。

**したがって受け入れ基準 2 は「確かめない」を選択し、上記 1〜3 を根拠として記録する。**

## テスト方針

`WolverinePipelineExtensionsTests` に、ダミー段 `GateProbeConsumer`（**型名を意図的に `Consumer` で終わらせる**
——decompile で確認した規約探索の名前サフィックス条件に一致させるため）を追加し、
`Wolverine.Configuration.HandlerDiscovery.FindCalls(WolverineOptions)`（internal。リフレクションで呼ぶ。
既存の `RegisteredTypes` ヘルパと同じ作法）を直接呼んで「規約探索 ＋ 明示登録を合成した実際の登録候補」を
取得し、次の 3 点を測る。

1. **前提の検証**: `GateProbeConsumer` はそもそも規約探索の対象になる型である
   （`AddPlatformWolverineStep` を呼ばずに `IncludeAssembly` するだけで拾われる）。
   これが無いと「探索が拾わない型を除外した」空証明になり得るため必須。
2. **規則 8（無効化）**: `enabled:false` で登録したとき、規約探索を効かせた状態でも `FindCalls` の結果に
   `GateProbeConsumer` が現れない。
3. **規則 9（対照）**: `enabled:true` では現れる（1・2 だけでは「常に見えない」実装でも通ってしまうため、
   対照条件が要る）。

### 変異試験

`WolverinePipelineExtensions.cs` 規則 8 の `options.Discovery.CustomizeHandlerDiscovery(...)` 呼び出しを
一時的にコメントアウトし（`IncludeType` を呼ばないだけの状態に戻す＝ `#441` E1 の本番欠陥そのもの）、
上記 2 の試験が**落ちること**を確認する。戻した後に通ることも確認する。

## 受け入れ基準への対応

- [x] `enabled:false` が段を止めることを、規約探索が効いている条件で測るテストを追加した
- [x] 除外を外す変異でテストが落ちることを実測した（環境非依存 —— `IncludeAssembly` で明示的に
      アセンブリを固定するため、実行環境の既定走査対象に依存しない）
- [x] MassTransit 版は「実ブローカで確かめない」と決定し、根拠を本書「MassTransit 版の扱い」節に記録した
- [x] 同型の穴の洗い出し結果を「母集合の確認」節に記録した（該当なし）
