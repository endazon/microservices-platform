---
title: ドリフト検出の応答期限切れ（HttpClient.Timeout）が BackgroundService を抜けて BFF のホストを止める退行を直す
issue: "#1382"
plan_refs:
  - FR-15
  - ADR-0018
adr_refs:
  - IADR-0029
  - IADR-0232
status: done
created: 2026-09-11
---

# 作業仕様書: ドリフト検出の timeout がホストを止める（#1382）

## 起点

- #1382（integration-stack が develop で連続失敗）。`check-stack-ready.js` G1 が `bff-service` の 2 Pod を Ready=False で落とし、
  Pod は `Back-off restarting failed container`。#1371 で待ちステップを非致命にした後も同じ形で赤い。

## 実測（run 34529974835・2026-09-10 21:03Z）

BFF コンテナのログ:

```
crit: Microsoft.Extensions.Hosting.Internal.Host[10]
      The HostOptions.BackgroundServiceExceptionBehavior is configured to StopHost. A BackgroundService has thrown an unhandled exception, and the IHost instance is stopping.
      System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 5 seconds elapsing.
         at ...Introspection.HttpEffectiveConfigCollector.CollectOneAsync(...)
         at ...Introspection.DriftDetectionHostedService.SafeRunOnceAsync(CancellationToken ct)
```

## 原因

- `HttpEffectiveConfigCollector.CollectOneAsync` と `DriftDetectionHostedService.SafeRunOnceAsync` は
  `catch (Exception ex) when (ex is not OperationCanceledException)` で「取り消しだけ外へ出す」。
- `HttpClient.Timeout` は **`TaskCanceledException`（`OperationCanceledException` の派生）**で表れるため、呼び出し側の ct が
  取り消していない期限切れまで素通しになる。起動直後に応答の遅いサービスが 1 つあれば、初回の即時検出（do-while 初回）で
  例外が `ExecuteAsync` を抜け、既定ホスト（StopHost）が BFF のプロセスごと止める。

## 設計

| 対象 | 変更 |
| --- | --- |
| `HttpEffectiveConfigCollector.CollectOneAsync` | フィルタを `ex is not OperationCanceledException \|\| !ct.IsCancellationRequested` に。**ct 由来でない取り消しは到達不能として隔離**し、他サービスの収集は続く |
| `DriftDetectionHostedService.SafeRunOnceAsync` | 同じフィルタ。二重の守り（collector を経ない `IDriftRunner` 実装でもループとホストを守る） |
| 試験 | collector: 期限切れ → `UnreachableServices`（陽性）／ ct 由来の取り消しは外へ出る（陰性対照）。hosted: `TaskCanceledException` でも `ExecuteTask` が faulted にならない（陽性）。既存「停止要求は外へ出さない」が陰性対照 |

`IntrospectionOptions.TimeoutSeconds`（既定 5 秒）は変えない——起動直後の遅延を隠すのではなく、期限切れを「到達不能」として
正しく分類する。

## 走査した母集合（規則 2・9）

`when (ex is not OperationCanceledException)` で `src/` を走査: 上記 2 箇所（変更）のほか、同型の握りは
`Platform.Shared.Infrastructure` 内に無い（他は `catch (OperationCanceledException)` で return する `SafeWaitAsync` 型で、
ct を渡しているので問題ない）。

## 受け入れ基準

- [x] 新規 3 試験を含む Introspection 59 試験が緑
- [x] 変異: 2 ファイルの修正を戻すと **新規の陽性 2 試験だけが赤**（57 合格 / 2 失敗）、戻すと 59 合格
- [x] `dotnet format --verify-no-changes` 差分なし
- [ ] develop の次の integration-stack 実行で G1 が bff-service を通す（着地後に観測。#1382 は着地で閉じ、再発なら自動で再起票される）
