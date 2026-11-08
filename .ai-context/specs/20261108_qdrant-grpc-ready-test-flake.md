---
title: 作業仕様書 — QdrantGrpcReadyTests の「接続を受けてすぐ切る相手への最初の呼び出し」が Internal で落ちる flake を、試験を実装の契約に合わせて是正する
type: spec
status: in-progress
related_ids: [NFR, FR-02, ADR-0009]
author: claude
created: 2026-11-08
updated: 2026-11-08
---

# 作業仕様書 — QdrantGrpcReadyTests の flake（Unavailable の期待が狭すぎる）

> 着手 2026-11-08（develop `59a542f53`）。実装の判断は伴わない（待機の判定は変えない）ので IADR は書かない。

## 起点（同型 2 回）

1. develop run 42175932124（job 127754266914、2026-11-06）
2. PR #2517 の head `e46ac276` の run 42433029088（job 128588269318。PR は Knowledge に触れていない）

どちらも `QdrantGrpcReadyTests.接続を受けてすぐ切る相手への最初の呼び出しはUnavailableで落ちる` の 31 行目で
`Expected ... StatusCode.Unavailable {value: 14}, but found StatusCode.Internal {value: 13}.`。

## 原因（実測と上流のコード）

- Grpc.Net.Client 2.83.0 の `GrpcProtocolHelpers.ResolveRpcExceptionStatusCode` は、送信の失敗の例外の連鎖に
  `SocketException` / `IOException` があれば `Unavailable`、どれも無ければ `Internal` を返す。
- 接続を受けてすぐ切る相手に対し、SocketsHttpHandler（.NET 10）が切断を観測する時機で連鎖が変わる。手元で 3000 回の呼び出しを数えた内訳:
  - preface の書き込み後に `NetworkStream` が破棄済み → `HttpRequestException ← IOException(handshake 未完了) ← ObjectDisposedException`（大半）→ Unavailable
  - 最初のフレームの読み込みで切断（`Connection reset` / `ResponseEnded`）→ `HttpIOException` を連鎖に持つ → Unavailable
  - preface の書き込み中に切断（`Broken pipe`）→ Unavailable
  - **preface の書き込みは通り、接続が pool へ渡った後・最初のストリームを予約する前に読み込みの側が切断を見て接続を閉じる**
    → `HttpConnectionPool.ReturnHttp2Connection` の `isNewConnection` の枝が**内側の例外を持たない**
    `HttpRequestException("...server did not complete the HTTP/2 handshake.")` を作る → **Internal**（約 1/1500〜1/3000）
- 待機の実装（`QdrantGrpcReady.ProbeAsync`）は `RpcException` を状態を問わず `false`（未起動）と読むので、Internal でも正しく未起動と読む。
  **穴は試験の期待（Unavailable だけ）の側にある。**

## 方式

- 試験に「まだ listen していない gRPC の口への最初の呼び出しが返す状態」の集合 `NotListeningYet = {Unavailable, Internal}` を置き、
  接続を受けてすぐ切る相手の試験はその集合の要素で落ちることを見る（性質＝最初の呼び出しは失敗する、は保つ）。
- 集合の各要素を**決定的に**起こす Theory を足す。実際の SocketsHttpHandler が投げる例外の形（接続の拒否・IOException・
  内側の例外なしの handshake 未完了）をそのまま投げる `HttpMessageHandler` を `GrpcChannel` に挟み、Grpc.Net.Client の写像を通す。
  - 最初の呼び出しが期待の状態で落ち、それが集合に含まれること。
  - `ProbeAsync` がどの形でも `false` と読むこと（そのため `ProbeAsync(QdrantClient)` の入口を足す。判定は変えない）。
- 誰も待ち受けていない口（接続の拒否）への最初の呼び出しが Unavailable で落ちる試験も足す（実通信で決定的）。
- 退けた案: SETTINGS_MAX_CONCURRENT_STREAMS=0 を返す受け口で Internal を実通信で起こす案。pool が SETTINGS の処理より先に
  ストリームを予約すると要求が応答待ちで止まる（負荷下で 120 回中 3 回、試験が終わらなかった）ので採らない。

## 受け入れ基準

- [x] 旧の期待のまま本文を 3000 回繰り返す一時の試験が、5 回の実行の 5 回とも Internal で落ちる（試行 25・23・156・930・1967 回目）。
- [x] 新の期待の同じ一時の試験が、5 回の実行（計 15000 呼び出し、うち Internal 7 回）で全部通り、`ProbeAsync` は毎回 false。
- [x] CPU 負荷（8 本の busy loop）をかけ、試験クラスを 6 並列で 300 回実行して全部通る（11 件 × 300）。
- [x] `ProbeAsync` を `catch (RpcException e) when (e.StatusCode == StatusCode.Unavailable)` に狭めると、Internal の Theory が赤になる。
- [x] `dotnet build src/knowledge/backend/backend.slnx`・Knowledge の試験（`Category!=Integration`）・`dotnet format --verify-no-changes` が通る。
- 一時の試験はコミットしない。

## 母集合（規則 9）

- `Unavailableで落ちる` / `StatusCode.Unavailable` で `src/knowledge` と `docs/` を走査した。実通信の相手に対して状態を期待する試験はこのファイルだけ（他の `StatusCode.Unavailable` は偽の invoker で `RpcException` を直接与える単体試験で、時機の揺れが無い）。
