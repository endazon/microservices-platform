---
title: 作業仕様書 — DocumentNormalized のハーネスの待ちを、MassTransit の待たない列挙と自前の上限つきポーリングだけにする（同期の列挙のデッドロックを避ける）（#2332）
type: spec
status: done
related_ids: [NFR, FR-01, FR-12]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2332"
---

# 作業仕様書 — ハーネスの待ちのデッドロック（#2332）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `40bc9820`）。

## 現状と原因

- CI（job 123724652810）で `DocumentService.Tests` が止まり、30 分で打ち切られた。結果を出さなかったのは `NormalizedAssetLedgerTests` の 5 本だけだった（ほかのクラスは試験の数と結果の数が一致）。
- 手元でもこのクラスだけを流すと数回に 1 回止まる。足した記録で、`DocumentNormalizedHarnessWait.UntilConsumedAsync` の `harness.Consumed.Select<DocumentNormalized>(ct).FirstOrDefault(...)` が、要素を 1 件見た後で戻らないと分かった。
- MassTransit 8.4.1 の `AsyncElementList.Select`（同期の列挙）は、次の形をしている（上流 `src/MassTransit/Testing/Implementations/AsyncElementList.cs`）。
  - `lock (_messages)` を握ったまま `yield return` する。
  - 記録が尽きると、取り消しの callback（`ct` とハーネスの非活動のトークン）を登録して `Monitor.Wait` で待つ。
  - callback は `lock (_messages)` を取る。列挙の破棄は `finally` で登録を `Dispose` し、登録の `Dispose` は実行中の callback の完了を待つ。
- 待つ段で届いた要素を `FirstOrDefault` が見ている間（lock を握っている）に非活動のトークンが取り消されると、callback は lock を待ち、こちらは破棄で callback の完了を待つ。**互いに待って止まる。上限は無い。**
- 決定的な再現: 専用のスレッドで列挙し、待つ段で届いた要素を握った状態で別スレッドから取り消すと、破棄が 10 秒で戻らなかった（手元。試験としては残さない —— 上流のライブラリの性質であり、本リポの試験の対象ではない）。
- この待ちは #2310（#2306）が足したものである。非活動の窓への依存は消したが、別の止まり方を持ち込んだ。

## 方針

1. ハーネスの記録は**待たない列挙**で読む。既に取り消されたトークン（`new CancellationToken(canceled: true)`）を渡すと、`Select` は手元の記録を返した直後に `yield break` し、callback を登録せず `Monitor.Wait` にも入らない。
2. 待ちは自前の上限（60 秒）つきのポーリングだけで行う（従前どおり 50 ms 間隔）。
3. 失敗の確かめ（`Published.Select<Fault<DocumentNormalized>>(ct).Any()`）も待たない列挙にする。消費の記録は consumer の完了の後に積まれるので、それを見た時点で、consumer が出す Fault の発行は済んでいる（従前は非活動の窓まで待ってから見ていた）。
4. 読み方を 1 か所（`Snapshot` の 2 つの関数）に寄せ、試験で固定する。

## 母集合（規則 9・10）

- **規則 9**: `git grep -n -E "(Consumed|Published|Sent)\s*\.Select"`（全ユニット・AST の submodule を含む）で走査した。ハーネスの同期の列挙の使い手は次のとおり。
  - `DocumentNormalizedHarnessWait.cs` の 2 か所 —— **直す**（待つ段で要素を受け取り、その場で破棄する。止まり得る形そのもの）。
  - ConversionService の 3 か所（`ConversionFigureCorrectionTests.cs:137`・`:468`、`MassTransitDocumentNormalizedPublisherTests.cs:50`）—— **直さない**。
    - `:137` と `:50` は、直前の `Published.Any<T>()` が要素の到着を確かめた後に `.First()` を取る。要素は手元の記録の段で返り、callback の登録より前に列挙が終わる。止まる形にならない。
    - `:468` は「発行が無い」ことを確かめる（`.Should().BeEmpty()`）。待つ段で要素が来れば試験はどのみち赤で、要素を握ったまま取り消しと重なる窓は、赤になる場合の中にしか無い。
  - ほかの命中（`RecordingMessageBus.cs` の注記・`McpToolCatalogIntegrationTests` の LINQ）はハーネスではない。
- **規則 10**: `DocumentNormalizedHarnessWait.cs` の注記（「その時点の記録の同期の列挙を自分の上限でポーリングするので窓に依存しない」）は、同期の列挙が待つ段を持つことを見落としていた。直す。`NormalizedAssetLedgerTests` の最後の試験の注記（「既定の窓では、Fault の同期の列挙が窓が切れるまで塞がり」）も、待たない列挙にすると当たらなくなるので直す。

## 受け入れ基準 → 試験

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| 記録の読み方が待たない（非活動の窓 60 秒・TestTimeout 30 秒のハーネスで、記録が空でも即時に戻る） | `DocumentNormalizedHarnessWaitTests`（読み 2 つ。上限 5 秒） | 赤（上限で打ち切り。同期の列挙は TestTimeout まで待つ） |
| 届いた記録は待たない読み方でも見える（陽性対照） | 同上 | — |
| `NormalizedAssetLedgerTests` を繰り返し流しても止まらない | 手元で 20 回 | 修正前は 8 回中 2 回止まった |
