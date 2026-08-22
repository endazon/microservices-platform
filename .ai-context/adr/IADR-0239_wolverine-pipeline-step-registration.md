---
title: IADR-0239 Wolverine 版の段登録は入力型を IPipelineStep<TIn> から取り、導出できないこと自体を起動失敗にする（MassTransit 経路の「素通り」を継承しない）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - FR-14
  - FR-15
  - ADR-0018
  - ADR-0027
  - IADR-0233
  - IADR-0234
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md
  - planning:projects/microservices-platform/06_technical/10_composability-design.md (§2 Subscribe / Process / Publish・§5 安全弁)
---

# IADR-0239 Wolverine 版の段登録経路の設計

## 状況

移行チェーンの W2（[IADR-0234](./IADR-0234_wolverine-migration-boundary-455-441.md) 決定 3）。
`pipeline.json` の宣言と実装を突き合わせる fail-fast を、**MassTransit を要求せずに**行う経路が要る。

### 継承してはならない挙動が 1 つある

既存の MassTransit 経路（`PipelineExtensions.cs:108-121`）は入力型を `IConsumer<TIn>` から導出する。

```csharp
var inputType = typeof(TConsumer).GetInterfaces()
    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
    ?.GetGenericArguments()[0];
if (string.IsNullOrEmpty(step.Input)) { throw ...; }   // 宣言側が空 → 落ちる
if (inputType is not null                              // 🔴 実装側が導出できないと照合ごとスキップ
    && !string.Equals(step.Input, inputType.Name, StringComparison.Ordinal)) { throw ...; }
```

**宣言側の空は落ちるが、実装側の導出失敗は黙って照合を飛ばす。** 登録は進み、ログは
**宣言された** `input` を「登録した」と出す —— 検証を飛ばした痕跡がどこにも残らない。

MassTransit 経路でこれが表面化していないのは、`IConsumer<TIn>` が事実上必ず在るからにすぎない
（実測: 段の実装 5 件すべてがジェネリック版を実装している）。**Wolverine 段にその保証は無い。**
同じ形を持ち込めば、`input` 照合は**永久に素通りする**。

### 自己申告側にも同じ形がある

`IntrospectionBuilder.AddStep`（`IntrospectionExtensions.cs:67-69`）は
`?.GetGenericArguments()[0].Name ?? string.Empty` で、導出できないと **`input` が空文字**になる。
これは `DriftDetector.cs:69-75` が実行時にドリフトとして検出する
（起動失敗ではなく、5 分後の Warning になる）。**本 PR の射程外**だが後述の結果に記録する。

## 決定

### 決定 1: 入力型の情報源を `IPipelineStep<TIn>` で与える

```csharp
public interface IPipelineStep<TIn> : IPipelineStep where TIn : class;
```

**追加であって変更ではない。** 既存の段は素の `IPipelineStep` ＋ `IConsumer<TIn>` のまま
1 バイトも変えない。新規の Wolverine 段だけが実装する。

`IPipelineStep` / `PipelineOptions` は元々トランスポート非依存であり（実測: `using MassTransit;` を
持つのは `PipelineExtensions` と `IntrospectionExtensions` だけ）、そのまま再利用できる。

### 決定 2: 入力型を導出できないこと自体を起動失敗にする

`AddPlatformWolverineStep<TStep>` の型制約は `class, IPipelineStep` だけ ——
**`IConsumer` を要求しない**（W2 の要件）。`IPipelineStep<TIn>` が無ければ **throw** する。
MassTransit 経路の `inputType is not null &&` は**継承しない**。

### 決定 3: 入力型を受け取るハンドラメソッドの存在まで見る

`IPipelineStep<TIn>` は型の**自己申告**にすぎない。これだけで `input` を突き合わせると
「宣言 対 別の宣言」の照合になり、**実装と突き合わせたことにならない**。
MassTransit 経路で照合が意味を持っていたのは `IConsumer<TIn>` が**実際のディスパッチ契約**だったからである。

よって Wolverine のハンドラメソッド（`Handle` / `Consume` ほか、`+ Async` を含む）で
`TIn` を受け取るものが在ることを要求する。**これは C3 の先取りではなく、決定 2 の照合を
空洞にしないための最小要件である。**

🔴 **ハンドラメソッド名は 11 個である。** `HandlerChain` の公開定数（`Handle` / `Handles` /
`Consume` / `Consumes`）だけでは足りず、実際の探索は `HandlerDiscovery` の内部一覧
（saga 系の `Orchestrate` / `Start` / `StartOrHandle` / `NotFound` を含む）を使う（実測）。
4 個に絞ると **Wolverine が受け付けるハンドラを本経路だけが拒否する**（偽の起動失敗）。
複写した一覧が版更新でずれたら落ちる追随試験を置く。

### 決定 4: `queue` は戻り値で呼び出し側へ返す

受信キューの設定（ADR-0027 手順 3）は `ListenToPlatformQueue`（U4 で封じ込め済み）が担うため、
本経路では行わない。ただし **`queue` 宣言を黙って無視すると「宣言したのに効かない」形**になるので、
解決済みの段宣言を戻り値として返し、呼び出し側が渡す。

### 決定 5: 照合は `Ordinal` である（大文字小文字を区別する）

`input` は C# の型名であり大文字小文字が意味を持つ。**変異試験で実測した穴を塞いだ決定である** ——
`OrdinalIgnoreCase` へ緩める変異は当初 **37 件すべて緑のまま素通り**した（検出力ゼロ）。
専用の試験を足し、同じ変異が 1 件を落とすことを再実測した。

## 結果

- Wolverine 段の登録経路ができ、E1 以降の辺の移行が着手可能になる。
- MassTransit 経路と安全弁（`PartialMigrationSafetyValveTests`）は無傷である
  （`PipelineExtensions.cs` / `IntrospectionExtensions.cs` の diff は空）。
- 🔴 **自己申告（`IntrospectionBuilder.AddStep`）は本 PR の射程外であり、Wolverine 段は未対応のままである。**
  Wolverine 段が `AddStep` を通ると `input` が空文字で自己申告され、`DriftDetector` が
  実行時にドリフトとして警告する。**E1（最初の辺）が Wolverine 段を作る前に対処が要る。**
  対処案は決定 1 と同じ情報源（`IPipelineStep<TIn>`）を `AddStep` の Wolverine 版が読むことである。
- 本経路はまだ**どのサービスからも呼ばれていない**（E1 が最初の呼び出し元になる）。

## 関連

- [IADR-0234](./IADR-0234_wolverine-migration-boundary-455-441.md)（W2 を定義。決定 4 が型制約の処分を C3 に置く）
- [IADR-0233](./IADR-0233_wolverine-shared-helper-confinement.md)（共通ヘルパの封じ込め・決定 6 の「検査の回避ではなく遵守」）
