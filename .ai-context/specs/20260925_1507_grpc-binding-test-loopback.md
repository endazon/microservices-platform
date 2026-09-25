---
title: Platform.Shared.Infrastructure.Tests の gRPC 待受の陰性対照を、0.0.0.0 で実際に待ち受けない形へ置き換える
type: spec
status: done
related_ids: [NFR-16, ADR-0029, ADR-0075, IADR-0379]
author: Claude
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_east-west-grpc.md
---

# gRPC 待受の陰性対照を 0.0.0.0 で待ち受けない形へ置き換える（#1507）

## 背景

オーナーの指摘（2026-09-25）: `Platform.Shared.Infrastructure.Tests` が 0.0.0.0 でホストされている。127.0.0.1 にすること。

`GrpcListenerBindingTests.Grpc_port_stays_reachable_from_outside_when_http_binds_all_interfaces` は、`http://*:0` で実際にホストを起動していた。そのため、h2c リスナが `ListenAnyIP`（全インタフェース）で待ち受け、試験を走らせた端末の外から届く状態を試験自身が作っていた。

この試験の目的（IADR-0379 決定 3 の対の主張）は、「ワイルドカードの構成なら、h2c ポートは全インタフェースへ開く」ことの確かめである。単に 127.0.0.1 へ置き換えると、何も確かめない試験になる。

## 母集合

- 走査: `Platform.Shared.Infrastructure.Tests` 配下で、`ListenAnyIP` / `IPAddress.Any` / `http://*:` / `http://+:` / `0.0.0.0` / `UseUrls` / `AddPlatformGrpcListener` / `PortKey` / `WebApplication.CreateBuilder`。
- 実際にソケットを開いて待ち受けていたのは、`GrpcListenerBindingTests` の 2 件だけ。
  - ループバックに絞る試験: `http://127.0.0.1:0` → 127.0.0.1。問題なし。
  - ワイルドカードの試験: `http://*:0` → 0.0.0.0。**是正の対象**。
- 除外（ソケットを開かないもの）:
  - `GrpcListenerExtensionsTests` の `http://+:8080` などは、構成の解決だけを見る文字列で、ホストを起動しない。
  - `CommonServiceExtensionsTests` / `HealthCheckExtensionsTests` は `UseTestServer`（インメモリ）。
- 射程外: 他のサービスの試験用起動器（`GrpcKestrelFactory` など）は別の issue で洗い出す。

## 是正

1. `GrpcListenerExtensions.Listen` の「どこへ待ち受けるか」の判定を、純粋な関数 `ResolveListenTarget` として切り出す。判定の中身は従前と同一で、本番の挙動は変わらない（ワイルドカード → AnyIP、`localhost` → Localhost、IP → 特定アドレス、それ以外 → AnyIP）。
2. ワイルドカードの試験は、ホストを起動せずに、`AddPlatformGrpcListener` と同じ関数の連鎖（HTTP 側の解決 → gRPC ホストの解決 → 待受先の判定）を通して `AnyIP` に倒れることを確かめる。`*` / `+` / `0.0.0.0` / `[::]` の 4 形を見る。
3. 判定が実際の bind に届いていることは、ループバックの試験が実ソケット（127.0.0.1）で確かめる（同じ `Listen` を通る）。
4. 対として、ループバックに絞った構成の判定が「ループバックの特定アドレス」になることも純粋な関数で確かめる。

## 受け入れ基準

- `Platform.Shared.Infrastructure.Tests` の試験が、0.0.0.0 / [::] で待ち受けない。
- ワイルドカードなら全インタフェース、ループバックならループバック、の両方向の主張が残っている。
- `dotnet test`（Platform.Shared.Infrastructure.Tests）が緑。
