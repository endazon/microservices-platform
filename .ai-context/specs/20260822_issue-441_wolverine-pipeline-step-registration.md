---
title: 作業仕様書 — pipeline.json 突合 fail-fast の Wolverine 版登録経路（IConsumer を要求しない IPipelineStep 登録。W2）
type: spec
status: done
related_ids:
  - NFR
  - FR-14
  - FR-15
  - ADR-0018
  - ADR-0027
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - "ADR-0027（メッセージング基盤 = Wolverine）"
  - "ADR-0018（構成情報 API・宣言的パイプライン）"
  - "planning:projects/microservices-platform/06_technical/10_composability-design.md（§2 Subscribe / Process / Publish）"
related_adrs:
  - IADR-0233
  - IADR-0234
  - IADR-0239
issue: "#441"
---

# 作業仕様書: Wolverine 版のパイプライン段登録経路（W2）

## 起点

- 移行チェーンの **W2**（[IADR-0234](../adr/IADR-0234_wolverine-migration-boundary-455-441.md) 決定 3 が定義）。前提は W1（着地済み・PR #922）。
- 実装 issue: **`#441`**。

## 🔴 本 PR は安全弁に触らない

`AddPlatformPipelineStep` / `IntrospectionBuilder.AddStep` の
`where TConsumer : class, IConsumer, IPipelineStep` は **C3 で処分する**（IADR-0234 決定 4）。
**本 PR は MassTransit 経路を 1 バイトも変えない。** 新経路は併存する別 API である（U4 と同じ作法）。

**自己判定基準: `PartialMigrationSafetyValveTests` が赤くなったら単位を踏み越えた合図である。**

## 🔴 本丸 —— `input` の導出が失敗したとき「素通り」してはならない

既存 MassTransit 経路（`PipelineExtensions.cs:107-121`）の実測:

```csharp
var inputType = typeof(TConsumer).GetInterfaces()
    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
    ?.GetGenericArguments()[0];
if (string.IsNullOrEmpty(step.Input))            // 宣言側が空 → 落ちる
{ throw new InvalidOperationException(...); }
if (inputType is not null                        // 🔴 実装側が導出できないと照合ごとスキップ
    && !string.Equals(step.Input, inputType.Name, StringComparison.Ordinal))
{ throw new InvalidOperationException(...); }
```

**宣言側の空は落ちるが、実装側の導出失敗は黙って照合を飛ばす。** Wolverine 経路には
`IConsumer<TIn>` が無いため、この形をそのまま持ち込むと **`input` 照合が常に素通りする**
（＝「緑になったが何も測っていない」）。**本 PR の設計上の要点はここ 1 点である。**

## 設計

### 1. `IPipelineStep<TIn>`（追加。既存実装には触らない）

```csharp
public interface IPipelineStep<TIn> : IPipelineStep where TIn : class;
```

既存の段は素の `IPipelineStep` ＋ `IConsumer<TIn>` のまま。**新規の Wolverine 段だけが実装する。**

### 2. `WolverinePipelineExtensions.AddPlatformWolverineStep<TStep>`

`where TStep : class, IPipelineStep` —— **`IConsumer` を要求しない**（W2 の要件そのもの）。
登録規則は MassTransit 経路と対応させるが、**規則 5・6 が新しい**。

| # | 規則 | 挙動 |
| --- | --- | --- |
| 1 | 宣言なし（`Steps` 空） | 既定で登録（現行配線と等価） |
| 2 | 段が未宣言 | 起動失敗 |
| 3 | `consumer` 宣言が空 / 型完全名と不一致 | 起動失敗 |
| 4 | `input` 宣言が空 | 起動失敗 |
| **5** | 🔴 **`IPipelineStep<TIn>` が無く入力型を導出できない** | **起動失敗**（MassTransit 経路は素通りする。ここが違い） |
| **6** | 🔴 **`TIn` を受ける Wolverine ハンドラメソッドが無い** | **起動失敗** |
| 7 | `input` 宣言と `TIn` 名が不一致 | 起動失敗 |
| 8 | `enabled: false` | 登録しない |
| 9 | `enabled: true` | `options.Discovery.IncludeType<TStep>()` |

**規則 6 を W2 の最小形に含める理由。** `IPipelineStep<TIn>` は型の**自己申告**にすぎない。
規則 6 が無いと、規則 7 は「宣言 対 別の宣言」の照合になり、**実装と突き合わせたことにならない**。
MassTransit 経路で照合が意味を持っていたのは `IConsumer<TIn>` が**実際のディスパッチ契約**だったからである。
規則 6 はその強度を回復するための最小要件であって、C3 の先取りではない。

🔴 **ハンドラメソッド名は 11 個である**（着手時に「4 個」と書いていたのは誤りで、実装前に訂正した）。
`HandlerChain` の公開定数は `Handle` / `Handles` / `Consume` / `Consumes` の 4 個だが、
**実際の探索が使うのは `HandlerDiscovery` の内部一覧**であり、saga 系
（`Orchestrate` / `Orchestrates` / `Start` / `Starts` / `StartOrHandle` / `StartsOrHandles` / `NotFound`）を
含む 11 個である（アセンブリから実測）。4 個に絞ると **Wolverine が受け付けるハンドラを本経路だけが
拒否する**（偽の起動失敗）。複写した一覧が版更新でずれたら落ちる追随試験を置く。
照合は「完全一致」か「+ `Async`」のみとする（`StartsWith` で緩く取ると無関係なメソッドを拾う）。

### 3. `queue` の扱い（意図的な境界）

MassTransit 経路の規則 6（`registration.Endpoint(e => e.Name = step.Queue)`）に対応する
受信キュー設定は、Wolverine では `ListenToPlatformQueue`（U4 で封じ込め済み・手順 3）が担う。
**本 PR は受信設定を行わず、解決済みの段宣言を戻り値として返す。**
`queue` 宣言を黙って無視すると「宣言したのに効かない」形になるため、**戻り値で呼び出し側へ渡す。**

### 4. 置き場

- 実装: `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Pipeline/`
- 試験: `src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests/Foundation/Pipeline/`
  （**既存テストプロジェクトへ追加**。新規プロジェクトはカバレッジ床を割る。#897 → #899 の実記録）
- **`using MassTransit;` を書かない**（残件 ratchet が 13 → 14 で fail する）。

## 受け入れ基準

- [x] `AddPlatformWolverineStep` が `IConsumer` を型制約に持たない
- [x] 規則 1〜9 すべてにテストがある
- [x] 🔴 規則 5（入力型を導出できない）が **throw** する。素通りしない
- [x] 🔴 規則 6（ハンドラメソッド不在）が **throw** する
- [x] `PartialMigrationSafetyValveTests` が緑のまま（安全弁に触っていない）
- [x] `PipelineExtensions.cs` / `IntrospectionExtensions.cs` の diff が空
- [x] 変異試験で「空 `input` に対して fail-fast が噛む」ことを実測した
- [x] 変異ごとに `git diff` で当該箇所のみ変化＋`Build succeeded`（EXIT=0）を先に確認した
- [x] 証明力の無い変異は「落ちなかった」ではなく**否定対照**として分類した

## 変異試験（計画）

| # | 変異 | 期待 |
| --- | --- | --- |
| A | 規則 5 を MassTransit 経路と同じ `is not null &&` 形へ退化させる | 入力型を導出できない段の試験が落ちる |
| B | `input` 照合の比較を落とす | 不一致の試験が落ちる |
| C | 規則 6（ハンドラメソッド検査）を外す | ハンドラ不在の試験が落ちる |
| D | 規則 7 の照合を `OrdinalIgnoreCase` へ緩める | **当初 0 件（＝穴）**。試験を追加して塞いだ |

**変異 A の判定では stderr の文言まで読む。** 別の規則（例: 規則 7 の不一致）で落ちて
「fail-fast が効いた」と誤読する事故を避けるため、**落ちた理由が入力型の導出失敗由来であること**を
例外メッセージで確認する。

## 変異試験の実測

基準は **38 件全通過**（本 PR 前は 24 件）。各変異とも **`Build succeeded`（EXIT=0）を先に確認**し、
`git diff` で当該箇所のみが変化したことを読んでからテスト結果を読んだ。復旧は `cmp` でバイト一致を確認した。

| # | 変異 | ビルド | 落ちたテスト | 落ちた理由（実測） |
| --- | --- | --- | --- | --- |
| A | 規則 5 を MassTransit 経路と同じ `is not null &&` 形へ退化させる | ✅ EXIT=0 | **2 件** — `規則5_入力型を導出できないなら起動失敗する` / `規則5_入力型を導出できない段は登録もされない` | `Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.` ＝ **例外が出ずに素通りした**。別の規則で落ちたのではない |
| B | 規則 7 の照合を常に真にする（`step.Input` → `inputType.Name`） | ✅ EXIT=0 | **1 件** — `規則7_input宣言が実装の購読イベントと不一致なら起動失敗する` | 同上（素通り） |
| C | 規則 6（ハンドラメソッド検査）を撤去 | ✅ EXIT=0 | **1 件** — `規則6_入力型を受けるハンドラメソッドが無ければ起動失敗する` | 同上（素通り） |
| D | 規則 7 の照合を `OrdinalIgnoreCase` へ緩める | ✅ EXIT=0 | 🔴 **当初 0 件（37/37 緑）** → 試験追加後 **1 件** | 検出力ゼロだった。`規則7_input宣言の大文字小文字が違えば起動失敗する` を足して再実測し、1 件落ちることを確認 |

🔴 **変異 D は「証明力の無い変異」ではなく、実在する検出の穴だった。** W1 の C′（Wolverine の
既定挙動と同じため何も変わらない変異）とは種類が違う。**記録して終わりにせず、試験を足して塞いだ。**

🔴 **変異 A の判定では例外メッセージまで読んだ。** 規則 5 の試験は
`WithMessage("*IPipelineStep<TIn>*")` と `Contain("入力イベント型を導出できません")` を課しており、
**別の規則で落ちたものを「fail-fast が効いた」と誤読しない**ようにしてある。

## 安全弁に触っていないことの実測

- `PartialMigrationSafetyValveTests` 3 件すべて緑。
- `git diff --stat -- PipelineExtensions.cs IntrospectionExtensions.cs` が **空**。

## 計画書との差異

**差異: なし。** 実装後に再確認した。ADR-0027 は「CQRS のローカルディスパッチも Wolverine の
ハンドラに統一する」と定めており、本経路はその登録を宣言と突き合わせるだけで、決定を変えていない。
ADR-0018（宣言的パイプライン）の fail-fast も**強める方向**（規則 5・6 の追加）である。

## 未決事項

1. 🔴 **自己申告（`IntrospectionBuilder.AddStep`）は Wolverine 段に未対応である。**
   `AddStep` は `IConsumer<>` から `input` を導出し、導出できないと **空文字**を自己申告する
   （`IntrospectionExtensions.cs:67-69`）。これを `DriftDetector.cs:69-75` が実行時にドリフトとして
   警告する（起動失敗ではない）。**E1 が最初の Wolverine 段を作る前に対処が要る。**
   対処案は本 PR と同じ情報源（`IPipelineStep<TIn>`）を Wolverine 版 `AddStep` が読むこと。
   **本 PR の射程外**としたのは、W2 の射程が登録経路であり、自己申告は別の面（FR-15）だからである。
   現時点で Wolverine 段は 1 つも存在しないため、**今壊れているものは無い。**
2. `queue` の受信設定を将来どの単位が行うか（E1 以降の辺の作業か、W3 の実ブローカ基盤か）。
3. 本経路はまだどのサービスからも呼ばれていない（E1 が最初の呼び出し元になる）。
