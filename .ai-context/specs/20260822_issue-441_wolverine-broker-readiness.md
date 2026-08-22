---
title: 作業仕様書 — Wolverine へ移した発行元のブローカ readiness を復元する（W4）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0027
  - ADR-0018
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - "ADR-0027（メッセージング基盤 = Wolverine）"
related_adrs:
  - IADR-0234
issue: "#441"
---

# 作業仕様書: Wolverine ブローカ readiness（W4）

## 起点

移行チェーンの **W4**（[IADR-0234](../adr/IADR-0234_wolverine-migration-boundary-455-441.md) 決定 3 の
チェーンへ追加する単位）。前提は W1。**E1 の前に入れる。**

## 🔴 なぜ要るか —— 移行すると readiness が黙って消える

`DataSourceService/Program.cs:26` は次を記録している。

> `#269: ブローカ疎通の readiness は MassTransit 組み込みの "masstransit-bus"（tag "ready"）で満たす。`
> `外部 AspNetCore.HealthChecks.Rabbitmq は RabbitMQ.Client 7 と非互換（TypeLoadException 'IModel'）のため使用しない。`

`AddMassTransit` はこのチェックを**暗黙に**登録する。Wolverine 側を実測した。

```
services.AddWolverine(opts => opts.UseRabbitMq(...));
→ registered health checks = 0
→ IHealthCheck descriptors = (なし)
```

**0 件である。** よって発行元を Wolverine へ移すと `/health/ready` は**ブローカが落ちていても 200 を返す**。
probe は「自分が主張していること」を検査しなくなる。k8s は publish できない pod へトラフィックを流す。

**これは E1 だけの話ではない。** 同じコメントは `DocumentService/Program.cs:29` と
`WikiService/Program.cs:27` にも在る（走査で確認）。E1〜E3b すべてに等しく効くため、
辺の移行とは別の単位にする（[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4）。

## 🔴 ドロップイン代替は無い（実測）

| 型 | public か | `IHealthCheck` を実装するか |
| --- | --- | --- |
| `ITransport.BuildHealthCheck(IWolverineRuntime)` → `WolverineTransportHealthCheck` | ✅ | —— |
| `WolverineTransportHealthCheck.CheckHealthAsync(CancellationToken)` → `Task<TransportHealthResult>` | ✅ | ❌ |
| `WolverineTransportHealthCheckAdapter`（`IHealthCheck` を実装する当のもの） | ❌ **internal** | ✅ |

`AddWolverineHealthCheck()` に相当する公開 API は無い。よって橋渡しを自分で書く。
**ただし公開 API だけで書ける**（実測）:

- `IWolverineRuntime.Options.Transports`（`TransportCollection`・public getter）
- `TransportHealthResult` は public record。`Status` は `TransportHealthStatus`（`Healthy=0` / `Degraded=1` / `Unhealthy=2`）、
  ほかに `Message` / `Protocol` / `TransportName` / `CheckedAt` / `Data` を持つ

## 設計

### 1. 置き場と形

`WolverineExtensions`（手順 3〜5 と retry/DLQ 既定を持つ共通ヘルパ）へ、
`IHealthChecksBuilder` の拡張として足す。

```csharp
public static IHealthChecksBuilder AddPlatformWolverineBroker(
    this IHealthChecksBuilder builder, string name = "wolverine-broker")
```

### 2. 🔴 **opt-in である。`AddPlatformHealthChecks` へ自動登録しない**

理由は 2 つあり、どちらも実測に基づく。

1. **並行 PR #931 の基準テストを黙って壊す。** 同 PR の
   `適用前の既定値_チェックが0件なら述語に関係なく両方200を返す` は
   `StartAsync(s => s.AddPlatformHealthChecks())` で**両方 200** を assert する。
   自動登録すると「0 件」の前提が偽になり、ブローカ不在で `ready` が 503 になって落ちる。
2. **ブローカを使わないサービスにまで付く。** `AddPlatformHealthChecks` の本番 call site は
   **12 箇所**、`UsePlatformMiddleware` は **11 箇所**（2026-08-22 に origin/develop で再実測。
   GraphService が #929 で着地して 11/10 → 12/11 になった分を含む）。
   🔴 **そのうち `UseWolverine` を配線しているものは 0 件である**（同日実測）。
   自動登録にすると **メッセージングと無関係な 12 サービスがブローカ停止で 503 を返す**。
   「壊れているのに 200 を返す」の逆で「無関係なサービスが騒ぐ」形だが、
   **どちらも readiness の意味を壊す**。よって **opt-in（S3）を採る。**

### 3. 既存シグネチャを変えない（＝利用側の追随が要らない）

`AddPlatformHealthChecks` / `MapPlatformHealthChecks` / `UsePlatformMiddleware` は**触らない**。
したがって GraphService（`3b3136ef` で着地。`AddPlatformAuth:17` / `AddPlatformHealthChecks:18` /
`AddPlatformIntrospection:39` / `UsePlatformMiddleware:54` / `MapPlatformHealthChecks:55` を使う）を含む
**12 の利用側（`UsePlatformMiddleware` は 11）は 1 行も変えない**。これは受け入れ基準で機械的に確かめる。

### 4. MassTransit 版との等価性

W1 で retry/DLQ の等価性を固定したのと同じ形にする。**「両方とも何か返す」では測ったことにならない。**

| 項目 | MassTransit（現行） | Wolverine（本 PR） |
| --- | --- | --- |
| タグ | `ready` | 同 `ready`（`MapPlatformHealthChecks` の `Predicate` が拾う唯一の条件） |
| ブローカ健全時 | Healthy → `/health/ready` 200 | 同 |
| ブローカ不達時 | Unhealthy → `/health/ready` **503** | 同 |
| `/health/live` への影響 | 無し（`Predicate = _ => false`） | 同 |

## 受け入れ基準

- [x] `AddPlatformHealthChecks` / `MapPlatformHealthChecks` / `UsePlatformMiddleware` の diff が空
- [x] 12 の利用側 `Program.cs`（`UsePlatformMiddleware` は 11）が 1 行も変わらない（GraphService を含む）
- [x] 既定では 0 件のまま（opt-in）—— `AddPlatformHealthChecks()` 単独では登録されない
- [x] 登録したチェックが `ready` タグを持つ
- [x] 🔴 **ブローカ不達で `/health/ready` が 503 を返すことを実 HTTP で測る**
- [x] 🔴 **ブローカ健全時に 200 を返すことを対で測る**（陽性対照。503 側だけでは
      「常に 503 を返す実装」と区別できない）
- [x] `/health/live` はどちらの場合も 200（`Predicate = _ => false` が効いている）
- [x] 変異試験で、上の 503 が実際に検出力を持つことを示す

## 🔴 完了条件 —— 実測で満たした

**ブローカへ到達できない状態で `/health/ready` が 503 を返すことを実ブローカで実測した。**

| 試験 | 結果 |
| --- | --- |
| `ブローカ健全時はreadyが200を返す_陽性対照` | ✅ |
| `起動後にブローカへ到達できなくなるとreadyが503になりliveは200のまま` | ✅（遮断から **3 秒**で 503） |

クラスタの RabbitMQ へ `kubectl port-forward` し、**自前の TCP 中継（`BrokerTcpGate`）を挟んで、
ブローカではなく中継だけを落とした**。共有クラスタの exchange / queue には一切触れていない。

### 🔴 起動時のブローカ障害は対象外である（完了条件を書き換えた理由）

当初は「起動時にブローカが落ちている状態で 503」を測るつもりだったが、**それは測れない**。
到達不能なブローカに対し Wolverine ホストは **20 回再試行して約 135 秒後に
`BrokerInitializationException` で起動に失敗する**（実測）。ホストが立たない以上 `/health/ready` は
存在せず、**試験対象が無い**。そのまま書けば「変異試験で確認済み」という嘘が残るところだった。

pod は crash loop になり、readiness ではなく起動そのもので止まる（それ自体は安全側）。
**readiness が守れるのは「起動後に到達できなくなる」場合だけ**である。

### ⚠️ 限界（正直に書く）

中継の切断は**ブローカのクラッシュとバイト等価ではない**（RST / 無応答 / 半開の違い）。
再現できたのは「確立済み接続が落ち、再接続もできない」形である。

## テスト方針と、#931 との順序

実 HTTP で 503 を測るには `Microsoft.AspNetCore.Mvc.Testing` と
`HealthCheckExtensionsTests.cs` が要る。**どちらも並行 PR #931 が持っている**
（同 PR が `Platform.Shared.Infrastructure.Tests.csproj` へ同パッケージを足し、
「失敗するチェックを登録 → live 200 / ready 503」の器を作っている）。

したがって本 PR は 2 段に分ける。

| 段 | 内容 | #931 依存 |
| --- | --- | --- |
| **A** | `WolverineExtensions` への実装 ＋ 単体試験（`ready` タグ・登録数・`TransportHealthStatus` の写像） | 無し |
| **B** | 実 HTTP による 503 / 200 の対 ＋ 変異試験 | **あり**（#931 マージ後） |

**段 B が入るまで本 PR は完了しない。**

## 変異試験（実測）

各変異とも **`BUILD_EXIT=0` を先に読み**、`git diff` で当該箇所のみの変化を確認し、
復旧は `cmp` でバイト一致を確認した。

| # | 変異 | ビルド | 落ちた試験 | 落ちた理由（実測） |
| --- | --- | --- | --- | --- |
| A | `ready` タグを外す | ✅ EXIT=0 | **1 件** | `expected 503 … but found 200` ＝ ブローカ不達なのに readiness が緑 |
| B | `Unhealthy` を握り潰す | ✅ EXIT=0 | **1 件** | 同上 |
| C | `ITransport` 経由の dispatch へ戻す（**最初に出荷したバグの再注入**） | ✅ EXIT=0 | **2 件** | `healthy broker … but found 503` ＝ 健全なのに恒久的に un-ready |

🔴 **変異 C が要である。** これが無いと、A と B が通ったまま
**「健全なブローカに対して恒久的に 503」** という状態が素通りする。
**落ちる側だけでなく通る側（陽性対照）も守られている**ことを、実際に出荷しかけたバグで示した。

## 計画書との差異

**差異: なし**（見込み）。実装後に再確認する。

## 未決事項

1. 段 B の着手は #931 のマージ待ち（`Microsoft.AspNetCore.Mvc.Testing` と
   `HealthCheckExtensionsTests.cs` を同 PR が持つため）。

## 🔴 新規 IADR を起こさない

W4 は移行チェーンへ単位を 1 つ足すが、**新しい実装 ADR は作らない。**
[IADR-0234](../adr/IADR-0234_wolverine-migration-boundary-455-441.md) 決定 3 がチェーンの定義そのものであり、
そこへ **日付つき追記**（`［2026-08-22 追記 / #441］`）で W4 を足すのが正しい記録先である
（凍結記録への経過追記は `traceability.repo.md` が認める形。W1 で IADR-0233 に対して行った先例と同じ）。

設計判断（opt-in／allowlist／例外を Unhealthy へ写す）は本仕様書が根拠つきで持つ。
**これにより本 PR は IADR 番号を消費せず、改番の連鎖に入らない。**
