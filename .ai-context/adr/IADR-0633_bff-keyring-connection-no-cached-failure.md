---
title: IADR-0633 BFF のセッションストアへの遅延接続は失敗を覚えない形（PublicationOnly）を 1 か所に置き、鍵リングとヘルスチェックが同じものを使う
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-07, NFR-18, ADR-0032, IADR-0251, IADR-0510, IADR-0522]
author: claude
created: 2026-10-25
updated: 2026-10-25
related_specs:
  - ../specs/20261025_2242_bff-keyring-connection-no-cached-failure.md
---

# IADR-0633: 鍵リングの接続は初回の失敗を覚えない（#2242）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-25
- 決定者: claude（#2242。オーケストレーターの裁定 2026-10-25: `BffSessionExtensions.cs:74` を `PublicationOnly` にし、`AbortOnConnectFail` の
  扱いを決めて根拠を残す）

## 起点・関連

- 起点 issue: #2242。発端は #2232 と #2241 の e2e-live（同型の事故 2 回目）。
- 関連: [IADR-0251](./IADR-0251_bff-session-token-handler.md) 決定 5（鍵リングの共有）・[IADR-0510](./IADR-0510_bff-keyring-storage-resolves-di-connection.md)
  （鍵リングの保存先は DI の接続を引く）・[IADR-0522](./IADR-0522_session-store-valkey-auth-required.md)（ヘルスチェックはセッションと同じ構成で接続する）。
- 作業仕様書: [20261025_2242_bff-keyring-connection-no-cached-failure](../specs/20261025_2242_bff-keyring-connection-no-cached-failure.md)。

## コンテキストと課題

使い捨てのスタックで、BFF が Running/Ready なのに `/bff/auth/login` が全部 500 を返すことが 2 回あった（#2232・#2241）。ログは
`KeyRingProvider` の `RedisConnectionException` の後に `OpenIdConnectHandler.WriteNonceCookie` の `CryptographicException`。同じ時間帯に
coredns の差し替えと flannel の `FailedCreatePodSandBox` が出ていた。

鍵リングの接続は `new Lazy<IConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(...))` で、既定の
`LazyThreadSafetyMode.ExecutionAndPublication` は**ファクトリの例外を覚える**。初回の `Connect` が 1 度失敗すると、そのプロセスの鍵リングは
以後ずっと例外になり、login の challenge（nonce の cookie を DataProtection で保護する）が 500 になる。一方、ヘルスチェックの接続
（`Program.cs`）は `PublicationOnly` で失敗を覚えないので、readiness は回復して Ready のままになる。方針が 2 か所に分かれていた。

## 決定

1. **失敗を覚えない遅延を 1 か所に置く**: `SessionStoreConnection.Lazy(connect)`（`PublicationOnly`）を `Foundation/Session/SessionStoreConnection.cs`
   に置き、鍵リング（`AddBffSession`）とヘルスチェック（`Program.cs`）の両方がこれを使う。`Platform.Bff` の本番コードで
   `IConnectionMultiplexer` の `Lazy` を作ってよいのはこのファイルだけとし、試験が走査で固定する。
2. **`AbortOnConnectFail` は据え置く（既定の true）**。
3. **接続を作る関数を DI に置く**（`SessionStoreConnect`。`TryAddSingleton`、既定は `ConnectionMultiplexer.Connect`）。試験は
   「つながらない間は例外、回復した後は偽の Valkey」を与え、本番の配線のまま login の 500 → 302 を測る。

## 理由

- 決定 1: 原因は「同じ置き場への接続の方針が 2 つあった」ことである。片方を直すだけでは、次に誰かが既定の `Lazy` を書けば戻る。
  置き場を 1 つにし、走査で外れを止める。
- 決定 2（`AbortOnConnectFail`）:
  - true のままなら、届かないときは `Connect` が例外になり、決定 1 の遅延が次の利用でやり直す。回復の経路は「次の要求で接続し直す」の 1 つで、
    試験（つながらない間は 500・回復後は 302）で固定できる。
  - false にすると `Connect` は届かなくても成功を返し、失敗は各操作（鍵リングの読み書き）へ散る。鍵リングの読み取りが空と区別しにくい形で
    失敗し、ヘルスチェック（同じ構成から作る）とも振る舞いが変わる。セッションの実体（`AddStackExchangeRedisCache`）は自前の接続を持つので、
    ここで変えても揃わない。今回の不具合は「失敗を覚える」ことだけで説明でき、false は要らない。
- 決定 3: 既存の試験は `IConnectionMultiplexer` ごと差し替えるので、本番の遅延を通らない（だから 2 回の事故まで捕まらなかった）。差し替える点を
  「接続を作る関数」まで下げ、遅延・保存先・challenge は本番のまま通す。

## 影響

- 競り合いで接続が 2 つ作られうる（`PublicationOnly` の性質）。使われるのは 1 つで、作られうるのは初回の利用の瞬間だけである。ヘルスチェックは
  従来から同じ性質を持っていた。
- `InMemoryRedisLists`（試験の偽の Valkey）は Dispose を受ける（DI のファクトリで作った接続はホストの破棄で Dispose される）。
- PoC は BFF の再配備だけで直る（設定・Secret・Valkey は変えない）。
